using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 会话记忆（docs/09 §6.13）：与知识库**分开存储**、按群隔离、检索走关键词，
/// 注入时由提示词标注「可能不准确」。
/// </summary>
public class MemoryStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-mem-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    private MemoryStore NewStore()
    {
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var store = new MemoryStore(db);
        store.EnsureCreated();
        return store;
    }

    [Fact]
    public void Search_IsScopedToChat()
    {
        var store = NewStore();
        store.Add("chat-a", "本群常用 MC 1.20.1 + Forge 47.2");
        store.Add("chat-b", "另一个群的服务器是 Fabric");

        var hits = store.Search("chat-a", "MC 版本");

        Assert.Single(hits);
        Assert.Contains("1.20.1", hits[0].Content);
    }

    [Fact]
    public void Search_RanksByTokenHits()
    {
        var store = NewStore();
        store.Add("c1", "用户偏好：回复简短");
        store.Add("c1", "用户偏好：回复简短，MC 1.20.1，Forge");
        store.Add("c1", "无关记录：今天天气不错");

        var hits = store.Search("c1", "MC 1.20.1 Forge 回复");

        Assert.NotEmpty(hits);
        Assert.Contains("Forge", hits[0].Content);
    }
}

/// <summary>会话切段时提炼记忆并落库（L2 轻量实现）。</summary>
public class SessionMemoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-memcut-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Cut_ExtractsFactsIntoMemory()
    {
        var config = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules = [new RouterRule { TaskType = TaskType.Summary, ProviderChain = ["p1"] }],
            ProviderMaxAttempts = 1,
        };
        var router = new ModelRouter(config, _ => new FakeClient("p1", true)
        {
            ResultOverride = request =>
            {
                var input = string.Join("\n", request.Messages.Select(m => m.Content));
                if (input.Contains("记忆提炼器", StringComparison.Ordinal))
                    return new ChatResult("用户偏好：回复简短\n环境：MC 1.20.1 + Forge 47.2", "p1", "m", 1, 1, TimeSpan.Zero);
                return new ChatResult("本次会话摘要", "p1", "m", 1, 1, TimeSpan.Zero);
            },
        });

        var db = new Database(_dbPath);
        db.EnsureCreated();
        var memory = new MemoryStore(db);
        memory.EnsureCreated();
        var manager = new SessionManager(new SessionStore(db), router, new AgentConfig(), memory);

        await manager.OnIncomingAsync(new BotMessage
        {
            MessageId = "m1",
            ChatId = "c1",
            ChatType = ChatType.Group,
            SenderId = "1",
            SenderName = "柠枺",
            Text = "以后回答简短点，我用的是 1.20.1 Forge",
        });
        await manager.CutAsync("c1", "manual");

        var hits = memory.Search("c1", "Forge 版本", 3);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Content.Contains("1.20.1"));
    }
}
