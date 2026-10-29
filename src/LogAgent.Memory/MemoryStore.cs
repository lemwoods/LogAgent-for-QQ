using System.Text;

namespace LogAgent.Memory;

/// <summary>
/// 会话记忆（L2 的轻量实现）：与知识库**分开存储**——
/// 知识库是审核过的确定事实，记忆只是「对话里出现过、可能过时或不准确」的记录。
/// 检索同样用搜索（词元命中数排序），但注入提示词时必须标明不确定性。
/// 按 chatId 隔离，避免跨群串记忆。
/// </summary>
public sealed class MemoryStore(Database db)
{
    public sealed record MemoryItem(long Id, string Content, string CreatedAt, int Score);

    public void EnsureCreated()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                chat_id TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_memory_chat ON memory_items(chat_id, id DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Add(string chatId, string content)
    {
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(content)) return;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_items (chat_id, content, created_at)
            VALUES (@c, @t, @ts);
            """;
        cmd.Parameters.AddWithValue("@c", chatId);
        cmd.Parameters.AddWithValue("@t", content.Trim());
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>按词元命中数检索本群最近 500 条记忆；没有命中返回空。</summary>
    public IReadOnlyList<MemoryItem> Search(string chatId, string query, int topK = 3)
    {
        var tokens = Tokens(query);
        if (tokens.Count == 0 || topK <= 0) return [];

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, created_at FROM memory_items
            WHERE chat_id = @c ORDER BY id DESC LIMIT 500
            """;
        cmd.Parameters.AddWithValue("@c", chatId);

        var hits = new List<MemoryItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var content = reader.GetString(1);
            var score = tokens.Count(t => content.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (score > 0)
                hits.Add(new MemoryItem(reader.GetInt64(0), content, reader.GetString(2), score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Id)
            .Take(topK)
            .ToList();
    }

    /// <summary>英文按词、中文按二元组切分（与知识库的思路一致，但实现独立，互不依赖）。</summary>
    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var ascii = new StringBuilder();
        var cjk = new StringBuilder();

        void FlushAscii()
        {
            if (ascii.Length >= 2) tokens.Add(ascii.ToString());
            ascii.Clear();
        }

        void FlushCjk()
        {
            for (var i = 0; i + 1 < cjk.Length; i++) tokens.Add(cjk.ToString(i, 2));
            cjk.Clear();
        }

        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) { FlushCjk(); ascii.Append(ch); }
            else if (ch >= 0x4E00 && ch <= 0x9FFF) { FlushAscii(); cjk.Append(ch); }
            else { FlushAscii(); FlushCjk(); }
        }
        FlushAscii();
        FlushCjk();

        return tokens.Distinct().Take(12).ToList();
    }
}
