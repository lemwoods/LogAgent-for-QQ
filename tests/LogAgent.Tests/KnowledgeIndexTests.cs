using LogAgent.Knowledge;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 知识库索引自检（docs/09 §6.8）：external-content FTS 的 COUNT(*) 读的是内容表，
/// 索引为空时仍显示「行数一致」——迁移数据库（丢 -wal）后检索会静默退化成 LIKE 乱召回。
/// 这里锁住「启动时能发现并重建」的行为。
/// </summary>
public class KnowledgeIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-kb-idx-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    private static ModelRouter Router() => new(new AppConfig()); // 无 provider → 纯词法检索

    private long FtsMatches(string token)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM knowledge_fts WHERE knowledge_fts MATCH $q";
        cmd.Parameters.AddWithValue("$q", $"\"{token}\"");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task EnsureCreated_RebuildsIndexWhenContentExistsButIndexIsEmpty()
    {
        var store = new KnowledgeStore(_dbPath, new KnowledgeConfig(), Router());
        store.EnsureCreated();
        await store.AddAsync(
            "java.lang.OutOfMemoryError: Java heap space —— 堆内存不足，调低 -Xmx 或换 64 位 Java",
            "manual", title: "内存不足");

        Assert.Equal(1, FtsMatches("outofmemoryerror"));

        // 模拟「拷了主库、丢了索引」：清空 FTS 索引，但内容表仍有数据
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO knowledge_fts(knowledge_fts) VALUES('delete-all')";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(0, FtsMatches("outofmemoryerror"));

        // 重新打开：探测到索引为空 → 重建
        var reopened = new KnowledgeStore(_dbPath, new KnowledgeConfig(), Router());
        reopened.EnsureCreated();

        Assert.Equal(1, FtsMatches("outofmemoryerror"));
        var hits = await reopened.SearchAsync("OutOfMemoryError heap", 3);
        Assert.Contains(hits, h => h.Item.Title == "内存不足");
    }
}
