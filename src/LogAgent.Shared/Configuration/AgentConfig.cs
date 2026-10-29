namespace LogAgent.Shared.Configuration;

/// <summary>Agent 行为与触发规则配置（规划 3.3 / M2）。</summary>
public sealed class AgentConfig
{
    /// <summary>群聊是否需要 @ 机器人才响应（私聊总是响应）。</summary>
    public bool NeedAtInGroup { get; set; } = true;

    /// <summary>额外触发关键词（即使未 @ 也响应）。</summary>
    public List<string> TriggerKeywords { get; set; } = [];

    /// <summary>会话超时切断（分钟，静默超过即切段）。</summary>
    public int SessionIdleTimeoutMinutes { get; set; } = 30;

    /// <summary>会话容量切断：token 估算上限。</summary>
    public int SessionMaxTokens { get; set; } = 8000;

    /// <summary>会话容量切断：消息数上限。</summary>
    public int SessionMaxMessages { get; set; } = 200;

    /// <summary>
    /// 单条消息的处理时限（秒）。超时中止并告知用户，避免一次故障把整个会话长时间占住。
    /// 默认 600s：分析 180s 超时 + 重试 180s + 自检仍有余量；调小会牺牲大日志分析的成功率。
    /// </summary>
    public int MessageTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 单条消息的 token 预算（含工具多轮与自检）。超过后中止本次处理并告知用户，不降级到其他模型。
    /// 默认 60000：正常图文问答远低于此值，多轮工具分析留足余量；设 0 或负数表示不限制。
    /// </summary>
    public int MessageMaxTokens { get; set; } = 60000;

    /// <summary>
    /// run_shell 是否先经 AI 安全审核（默认开启）。关闭后只剩沙箱白名单/黑名单，
    /// 适用于本地调试或已完全信任模型输出的场景。
    /// </summary>
    public bool ShellReviewEnabled { get; set; } = true;

    /// <summary>切段时是否生成 L1 摘要（走 Summary 分流，便宜模型）。</summary>
    public bool SummaryOnCut { get; set; } = true;
}
