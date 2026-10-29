using System.Collections.Concurrent;
using LogAgent.Routing;
using LogAgent.Shared;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;

namespace LogAgent.Memory;

/// <summary>喂给 Agent 的会话上下文：近期 L1 摘要 + 当前窗口 L0。</summary>
public sealed record SessionContext(
    string SessionId,
    string ChatId,
    IReadOnlyList<string> RecentSummaries,
    IReadOnlyList<ChatTurn> WindowTurns);

/// <summary>
/// 会话生命周期管理（规划 3.3）：每 chat 一个活跃会话；
/// 切断规则：超时/容量/手动；切断时生成 L1 摘要（Summary 分流，便宜模型）；
/// L0 原文实时落库（训练数据原料，永不丢弃）。
/// </summary>
public sealed class SessionManager(SessionStore store, ModelRouter router, AgentConfig config,
    MemoryStore? memory = null)
{
    /// <summary>底层存储（本机对话页回显历史等只读用途）。</summary>
    public SessionStore Store => store;

    private sealed class ActiveSession
    {
        public required string Id { get; init; }
        public required string ChatId { get; init; }
        public List<ChatTurn> Window { get; } = [];
        public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.Now;
        public int TokenEstimate { get; set; }
        public int MessageCount { get; set; }
    }

    /// <summary>
    /// 活跃会话表。值用 <see cref="Lazy{T}"/> 包一层，保证 <c>CreateSession</c> 只执行一次——
    /// 两条并发首消息若各自 miss 后各建一行，失败那行永远不会 CloseSession（孤儿会话，end_time 恒为 NULL）。
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<ActiveSession>> _active = new();

    public event EventHandler<string>? SessionLog;

    /// <summary>活跃会话的 chat 列表（UI 手动切断用）。</summary>
    public IReadOnlyList<string> GetActiveChats() => [.. _active.Keys];

    /// <summary>新消息进入：静默超时检测 → 容量切断 → 落库 L0 → 更新窗口。</summary>
    public async Task<SessionContext> OnIncomingAsync(BotMessage message, CancellationToken ct = default)
    {
        var idle = TimeSpan.FromMinutes(Math.Max(1, config.SessionIdleTimeoutMinutes));
        if (TryGetActive(message.ChatId) is { } stale &&
            DateTimeOffset.Now - stale.LastActiveAt > idle)
        {
            Log($"[{message.ChatId}] 静默超时，切段");
            await CutAsync(message.ChatId, "idle", ct);
        }

        var session = GetOrCreate(message.ChatId, message.ChatType);

        // 容量切断：本条消息将超限时，先切段再进新会话
        var incomingTokens = EstimateTokens(message.Text);
        if (session.TokenEstimate + incomingTokens > config.SessionMaxTokens ||
            session.MessageCount + 1 > config.SessionMaxMessages)
        {
            Log($"[{message.ChatId}] 会话超限（tokens≈{session.TokenEstimate}, msgs={session.MessageCount}），切段");
            await CutAsync(message.ChatId, "capacity", ct);
            session = GetOrCreate(message.ChatId, message.ChatType);
        }

        session.Window.Add(ChatTurn.User($"{message.SenderName}: {message.Text}"));
        session.TokenEstimate += incomingTokens;
        session.MessageCount++;
        session.LastActiveAt = DateTimeOffset.Now;
        store.AppendRawMessage(session.Id, message);

        return BuildContext(session);
    }

    /// <summary>Agent 回复进入窗口并记录 Turn（quality_tag 初始 none，后续反馈更新）。</summary>
    public void OnAgentReply(BotMessage message, string reply, string? modelUsed)
    {
        if (TryGetActive(message.ChatId) is not { } session) return;
        session.Window.Add(ChatTurn.Assistant(reply));
        session.TokenEstimate += EstimateTokens(reply);
        store.RecordTurn(session.Id, message.Text, reply, modelUsed: modelUsed);
    }

    /// <summary>手动/规则切段：生成 L1 摘要 → 关闭会话。摘要失败不阻断切段。</summary>
    public async Task<string?> CutAsync(string chatId, string reason, CancellationToken ct = default)
    {
        if (!_active.TryRemove(chatId, out var lazy)) return null;
        var session = lazy.Value;
        string? summary = null;
        if (config.SummaryOnCut && session.Window.Count > 0)
        {
            try
            {
                summary = await SummarizeAsync(session, ct);
            }
            catch (Exception ex)
            {
                Log($"[{chatId}] 摘要生成失败（不影响切段）：{ex.Message}");
            }
        }
        store.CloseSession(session.Id, DateTimeOffset.Now, reason, summary);
        if (summary is not null) store.InsertSummary(session.Id, 1, summary);
        if (memory is not null && session.Window.Count > 0)
            await ExtractMemoryAsync(session, ct);
        Log($"[{chatId}] 会话已切段（{reason}），消息 {session.MessageCount} 条，摘要{(summary is null ? "跳过" : "已生成")}");
        return session.Id;
    }

    /// <summary>
    /// 从会话窗口提炼「值得长期记住的事实」，交给 memoryWrite 写入可检索存储（知识库 source=memory）。
    /// 失败不影响切段；最多 3 条，过短/过长/「无」都丢弃。
    /// </summary>
    private async Task ExtractMemoryAsync(ActiveSession session, CancellationToken ct)
    {
        try
        {
            var transcript = string.Join("\n",
                session.Window.TakeLast(40).Select(t => $"{t.Role}: {t.Content}"));
            if (transcript.Length > 6000) transcript = transcript[^6000..];

            var result = await router.CompleteAsync(
                TaskType.Summary,
                [ChatTurn.System(Prompts.MemoryFactExtractor), ChatTurn.User(transcript)],
                maxTokens: 300, temperature: 0.1, ct: ct);

            var written = 0;
            foreach (var raw in result.Content.Split('\n'))
            {
                var fact = raw.Trim().TrimStart('-', '·', '*', ' ').Trim();
                if (fact.Length is < 6 or > 200) continue;
                if (fact is "无" or "无。") continue;
                memory!.Add(session.ChatId, fact);
                if (++written >= 3) break;
            }
            if (written > 0) Log($"[{session.ChatId}] 已写入 {written} 条会话记忆");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"[{session.ChatId}] 记忆提炼失败（不影响切段）：{ex.Message}");
        }
    }

    private async Task<string> SummarizeAsync(ActiveSession session, CancellationToken ct)
    {
        var transcript = string.Join("\n", session.Window.Select(t => $"{t.Role}: {t.Content}"));
        var result = await router.CompleteAsync(
            TaskType.Summary,
            [ChatTurn.System(Prompts.SessionSummary), ChatTurn.User(transcript)],
            maxTokens: 512, temperature: 0.2, ct: ct);
        return result.Content.Trim();
    }

    private SessionContext BuildContext(ActiveSession session)
    {
        var window = session.Window.TakeLast(24).ToList();
        var summaries = store.GetRecentSummaries(session.ChatId, 3);
        return new SessionContext(session.Id, session.ChatId, summaries, window);
    }

    /// <summary>取活跃会话（不存在或创建失败则返回 null）。</summary>
    private ActiveSession? TryGetActive(string chatId) =>
        _active.TryGetValue(chatId, out var lazy) ? lazy.Value : null;

    private ActiveSession GetOrCreate(string chatId, ChatType chatType)
    {
        try
        {
            return _active.GetOrAdd(chatId, k => new Lazy<ActiveSession>(() =>
            {
                var id = store.CreateSession(k, chatType);
                return new ActiveSession { Id = id, ChatId = k };
            })).Value;
        }
        catch
        {
            // Lazy 会缓存异常：建会话失败时把条目摘掉，避免该群后续所有消息永久失败
            _active.TryRemove(chatId, out _);
            throw;
        }
    }

    /// <summary>粗略 token 估算：中文≈字数，英文≈字符/4，取中庸。</summary>
    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 2);

    private void Log(string line) => SessionLog?.Invoke(this, line);
}
