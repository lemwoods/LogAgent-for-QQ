using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;

namespace LogAgent.Core;

/// <summary>
/// 消息泵（M4）：触发判定 → 消息富化（链接/附件抓取）→ 会话管理 → 命令路由（新话题/记住/学习网页/日志分析/对话）→ RAG 增强回复。
/// </summary>
public sealed class AgentRuntime(
    GroupContextAgent groupAgent,
    LogAnalysisAgent logAgent,
    LearningAgent? learningAgent,
    SessionManager sessions,
    AgentConfig agentConfig,
    MessageEnricher? enricher = null,
    TimeSpan? enrichLinkBudget = null)
{
    private static readonly string[] NewTopicCommands = ["新话题", "换个话题", "/new", "重置上下文"];
    private const int SegmentLength = 1600;

    /// <summary>
    /// 单条消息里「链接抓取」的总预算。富化在会话门内执行，外链卡住会连带阻塞该群后续消息，
    /// 因此给链接单独设上限（附件是用户主动上传，另按下载预算走）。
    /// </summary>
    private static readonly TimeSpan DefaultEnrichLinkBudget = TimeSpan.FromSeconds(90);

    private readonly MessageEnricher _enricher = enricher ?? new MessageEnricher();

    /// <summary>
    /// 每个会话一把门：同一 chat 的消息串行处理。
    /// 三个宿主（WPF / Avalonia / Headless）的消息事件都是 async void，收到消息就并发进入本类，
    /// 不加门时同一群的两条消息会并发改写 SessionManager 的窗口、切段与摘要状态。
    /// 门按 chatId 常驻（量级 = 活跃聊天数，不做回收，避免引用计数带来的复杂度）。
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatGates = new(StringComparer.Ordinal);

    public event EventHandler<string>? RuntimeLog;

    public SessionManager Sessions => sessions;

    /// <summary>
    /// 消息入口：先取该 chat 的门，再交给 <see cref="HandleCoreAsync"/>。
    /// 同一群串行、不同群并行；等待期间被取消则直接丢弃该条消息（不回复）。
    /// </summary>
    public async Task OnMessageAsync(IMessageSender sender, BotMessage message, CancellationToken ct = default)
    {
        var gate = _chatGates.GetOrAdd(message.ChatId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, agentConfig.MessageTimeoutSeconds)));
        if (agentConfig.MessageMaxTokens > 0) TokenBudget.Begin(agentConfig.MessageMaxTokens);
        try
        {
            await HandleCoreAsync(sender, message, deadline.Token);
        }
        catch (TokenBudgetExceededException ex)
        {
            RuntimeLog?.Invoke(this, $"[预算] 已用 {ex.Used}/{ex.Limit} tokens，中止");
            await sender.SendTextAsync(message.ChatId, message.ChatType,
                $"这条消息的模型用量超过预算（{ex.Used}/{ex.Limit} tokens），已中止。可以把日志拆小一点，或调大配置里的 MessageMaxTokens。",
                message.MessageId, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RuntimeLog?.Invoke(this, $"[超时] 处理超过 {agentConfig.MessageTimeoutSeconds}s，已中止");
            await sender.SendTextAsync(message.ChatId, message.ChatType,
                $"这条处理超过 {agentConfig.MessageTimeoutSeconds} 秒，已中止。可以把日志拆小一点，或稍后再发一次。",
                message.MessageId, ct);
        }
        finally
        {
            TokenBudget.End();
            gate.Release();
        }
    }

    private async Task HandleCoreAsync(IMessageSender sender, BotMessage message, CancellationToken ct = default)
    {
        RuntimeLog?.Invoke(this, $"[{message.ChatType}:{message.ChatId}] {message.SenderName}: {message.Text}");

        // 手动切断命令
        if (IsNewTopicCommand(message))
        {
            await sessions.CutAsync(message.ChatId, "manual", ct);
            await sender.SendTextAsync(message.ChatId, message.ChatType, "好的，已开启新话题 ✅", message.MessageId, ct);
            return;
        }

        // 消息富化：logshare 等链接 / QQ 文件附件 → 抓取正文并入消息（供会话窗口 / RAG / 日志分析共用）
        message = await EnrichAsync(message, ct);

        var context = await sessions.OnIncomingAsync(message, ct);

        // 学习命令（无需 @，需要已配置学习 Agent）
        if (learningAgent is not null && TryParseLearnCommand(message.Text, out var learnKind, out var learnArg))
        {
            try
            {
                var ack = learnKind == LearnCommandKind.Remember
                    ? await learningAgent.LearnFromUserAsync(learnArg, message.SenderName, ct)
                    : await learningAgent.LearnFromUrlAsync(learnArg, ct);
                sessions.OnAgentReply(message, ack, modelUsed: "learning");
                await sender.SendTextAsync(message.ChatId, message.ChatType, ack, message.MessageId, ct);
            }
            catch (Exception ex) when (ex is not TokenBudgetExceededException)
            {
                await sender.SendTextAsync(message.ChatId, message.ChatType, $"学习失败：{ex.Message}", message.MessageId, ct);
            }
            return;
        }

        if (!ShouldReply(message))
        {
            RuntimeLog?.Invoke(this, "未命中触发条件，仅记录不回复");
            return;
        }

        try
        {
            // 日志分析路由：明确请求分析 + 内容像日志
            string reply;
            if (IsLogAnalysisRequest(message.Text, out var logText))
            {
                RuntimeLog?.Invoke(this, "命中日志分析，交给 LogAnalysisAgent");
                reply = await logAgent.AnalyzeAsync(logText, message.SupportsMarkdown, ct);
            }
            else
            {
                reply = await groupAgent.ReplyAsync(message, context, message.SupportsMarkdown, ct);
            }
            sessions.OnAgentReply(message, reply, modelUsed: null);
            RuntimeLog?.Invoke(this, $"回复: {reply[..Math.Min(80, reply.Length)]}");
            await SendReplyAsync(sender, message, reply, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 处理时限到 / 停机取消：交给上层，不吞成“打嗝”
        }
        catch (TokenBudgetExceededException)
        {
            throw; // 预算击穿：交给上层给出明确提示
        }
        catch (Exception ex)
        {
            RuntimeLog?.Invoke(this, $"回复失败：{ex.GetType().Name}: {ex.Message}");
            await sender.SendTextAsync(message.ChatId, message.ChatType,
                $"呜，小炎内部打了个嗝（已记录日志，稍后再试）：{ex.Message}", message.MessageId, ct);
        }
    }

    /// <summary>消息富化：解析可抓取的链接（LogShare）与 QQ 文件附件；失败不阻断主流程。</summary>
    private async Task<BotMessage> EnrichAsync(BotMessage message, CancellationToken ct)
    {
        var hasLinks = message.Text.Contains("logshare.cn", StringComparison.OrdinalIgnoreCase);
        var hasAttachments = message.Attachments is { Count: > 0 };
        if (!hasLinks && !hasAttachments) return message;
        try
        {
            var enriched = await _enricher.EnrichTextAsync(message, ct, enrichLinkBudget ?? DefaultEnrichLinkBudget);
            if (enriched == message.Text) return message;
            RuntimeLog?.Invoke(this, $"[富化] 链接/附件已并入消息（+{enriched.Length - message.Text.Length} 字符）");
            return new BotMessage
            {
                MessageId = message.MessageId,
                ChatId = message.ChatId,
                ChatType = message.ChatType,
                SenderId = message.SenderId,
                SenderName = message.SenderName,
                Text = enriched,
                MentionedMe = message.MentionedMe,
                Timestamp = message.Timestamp,
                RawJson = message.RawJson,
                Attachments = message.Attachments,
                ChannelId = message.ChannelId,
                ChannelName = message.ChannelName,
                SupportsMarkdown = message.SupportsMarkdown,
            };
        }
        catch (Exception ex)
        {
            RuntimeLog?.Invoke(this, $"[富化] 失败（忽略）：{ex.Message}");
            return message;
        }
    }

    /// <summary>日志分析请求判定："分析/排查/看看日志"意图 + 多行/含错误标记的内容。</summary>
    private static bool IsLogAnalysisRequest(string text, out string logText)
    {
        logText = "";
        var hasIntent = (text.Contains("分析", StringComparison.Ordinal)
                         || text.Contains("排查", StringComparison.Ordinal)
                         || text.Contains("看看日志", StringComparison.Ordinal)
                         || text.Contains("看下日志", StringComparison.Ordinal)
                         || text.Contains("看一下日志", StringComparison.Ordinal))
            && (text.Contains("日志", StringComparison.Ordinal) || text.Contains("log", StringComparison.OrdinalIgnoreCase)
                || text.Contains("报错", StringComparison.Ordinal));
        var body = ExtractLogBody(text);
        var looksLikeLog = body.Contains('\n') && body.Split('\n').Length >= 3
            || body.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Exception", StringComparison.OrdinalIgnoreCase);
        if (hasIntent && looksLikeLog)
        {
            logText = body;
            return true;
        }
        return false;
    }

    /// <summary>去掉"分析日志"之类的指令前缀，保留日志正文。</summary>
    private static string ExtractLogBody(string text)
    {
        var idx = text.IndexOfAny(['\n']);
        var firstLine = (idx < 0 ? text : text[..idx]).Trim();
        var rest = idx < 0 ? "" : text[(idx + 1)..];
        // 第一行只是指令（短且含"分析/日志/报错"），正文从第二行起
        if (firstLine.Length <= 30 && (firstLine.Contains("分析") || firstLine.Contains("日志") || firstLine.Contains("log") || firstLine.Contains("报错")))
            return rest.Trim();
        return text.Trim();
    }

    /// <summary>
    /// 回复发送策略（柠枺要求：合并发送 + 分节格式化）：
    /// ① 文本含 ≥2 个【标题】分节 → 合并发送（OneBot 发转发卡片；官方 Bot 合并成一条 Markdown/文本）；
    /// ② 无分节但超长 → 按行切块后交给同一合并入口；
    /// ③ 通道既不支持转发也不支持合并（本机对话/超长）→ 短文直发、长文回退分段。
    /// </summary>
    private static async Task SendReplyAsync(IMessageSender sender, BotMessage message, string text, CancellationToken ct)
    {
        var sections = SplitSections(text);
        if (sections.Count >= 2 &&
            await sender.TrySendForwardAsync(message.ChatId, message.ChatType, sections, message.MessageId, ct))
            return;

        if (text.Length > SegmentLength &&
            await sender.TrySendForwardAsync(message.ChatId, message.ChatType, ChunkForForward(text), message.MessageId, ct))
            return;

        if (text.Length <= SegmentLength)
        {
            await sender.SendTextAsync(message.ChatId, message.ChatType, text, message.MessageId, ct);
            return;
        }

        var segments = Split(text, SegmentLength);
        for (var i = 0; i < segments.Count; i++)
        {
            var body = segments.Count > 1 ? $"({i + 1}/{segments.Count}) {segments[i]}" : segments[i];
            await sender.SendTextAsync(message.ChatId, message.ChatType, body, message.MessageId, ct);
            if (i < segments.Count - 1) await Task.Delay(350, ct);
        }
    }

    /// <summary>
    /// 分节标题：行首【标题】（后接正文），或独立成行的常用裸标题（部分模型不守括号格式）。
    /// </summary>
    private static readonly Regex SectionHeaderPattern = new(
        @"^(?:【[^】]{1,14}】.*|##[ \t]+[^\r\n]{1,50}|(?:结论|最终结论|原因分析|可能原因|根因分析|根因|解决方法|解决方案|修复方法|修复步骤|相关建议|建议|需要补充|补充信息|总结)\s*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// 把模型输出按行首【标题】拆成分节（≥2 节才有意义，否则返回空列表 = 不分节）。
    /// 首个标题前的内容作为"概览"节保留。
    /// </summary>
    public static IReadOnlyList<string> SplitSections(string text)
    {
        var sections = new List<string>();
        var matches = SectionHeaderPattern.Matches(text);
        if (matches.Count < 2) return sections;

        var preamble = text[..matches[0].Index].Trim();
        if (preamble.Length > 0) sections.Add(preamble);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body = text[start..end].Trim();
            if (body.Length == 0) continue;
            if (body.StartsWith("## ", StringComparison.Ordinal))
            {
                // 官方 Bot 的 Markdown 标题保持原样
            }
            else if (!body.StartsWith("【", StringComparison.Ordinal))
            {
                // 裸标题行 → 规范化为【标题】，卡片各页样式统一
                var nl = body.IndexOf('\n');
                var head = (nl < 0 ? body : body[..nl]).Trim();
                body = $"【{head}】{(nl < 0 ? "" : body[nl..])}";
            }
            sections.Add(body);
        }
        return sections;
    }

    /// <summary>无标题的长文：按行边界切成转发节点（每节点约 1200 字）。</summary>
    private static List<string> ChunkForForward(string text)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length + line.Length + 1 > 1200 && current.Length > 0)
            {
                chunks.Add(current.ToString().TrimEnd());
                current.Clear();
            }
            current.AppendLine(line);
        }
        if (current.Length > 0) chunks.Add(current.ToString().TrimEnd());
        return chunks;
    }

    private static List<string> Split(string text, int size)
    {
        var segments = new List<string>();
        for (var i = 0; i < text.Length; i += size)
            segments.Add(text.Substring(i, Math.Min(size, text.Length - i)));
        return segments;
    }

    private static bool IsNewTopicCommand(BotMessage m)
    {
        var text = m.Text.Trim().TrimStart('@', ' ');
        return NewTopicCommands.Contains(text, StringComparer.OrdinalIgnoreCase);
    }

    private enum LearnCommandKind { Remember, LearnUrl }

    /// <summary>识别学习命令："记住 XXX" / "learn XXX" / "学习 https://…"。</summary>
    private static bool TryParseLearnCommand(string text, out LearnCommandKind kind, out string arg)
    {
        kind = default;
        arg = "";
        var trimmed = text.Trim().TrimStart('@', ' ');
        if ((trimmed.StartsWith("记住") || trimmed.StartsWith("learn ", StringComparison.OrdinalIgnoreCase)) &&
            trimmed.Length > 4)
        {
            kind = LearnCommandKind.Remember;
            arg = trimmed.StartsWith("记住") ? trimmed["记住".Length..].TrimStart(':', '：', ' ') : trimmed[6..].Trim();
            return arg.Length > 0;
        }
        if (trimmed.StartsWith("学习 ") && trimmed.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            kind = LearnCommandKind.LearnUrl;
            arg = trimmed["学习 ".Length..].Trim();
            return Uri.TryCreate(arg, UriKind.Absolute, out _);
        }
        return false;
    }

    private bool ShouldReply(BotMessage m)
    {
        if (m.ChatType == ChatType.Private) return true;
        if (m.MentionedMe) return true;
        return agentConfig.TriggerKeywords.Any(k =>
            !string.IsNullOrEmpty(k) && m.Text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
