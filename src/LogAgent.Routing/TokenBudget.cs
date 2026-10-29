namespace LogAgent.Routing;

/// <summary>单条消息的 token 预算被击穿。与普通失败不同：不重试、不切换 provider，直接中止。</summary>
public sealed class TokenBudgetExceededException(long limit, long used)
    : Exception($"单条消息已用 {used} tokens，超过预算 {limit}")
{
    public long Limit { get; } = limit;
    public long Used { get; } = used;
}

/// <summary>
/// 消息级 token 计量（AsyncLocal，随异步流传播）。
/// 由 <see cref="ModelRouter"/> 在每次成功补全后累加，并在下一次调用前检查——
/// 超限时抛 <see cref="TokenBudgetExceededException"/>，由 AgentRuntime 中止并告知用户。
/// 没有 <see cref="Begin"/> 的调用（测试、单次 CLI）不受限制。
/// </summary>
public static class TokenBudget
{
    private sealed class State(long limit)
    {
        public long Limit { get; } = limit;
        public long Used;
    }

    private static readonly AsyncLocal<State?> Current = new();

    public static void Begin(long limit) => Current.Value = new State(limit);

    public static void End() => Current.Value = null;

    public static long Used => Current.Value?.Used ?? 0;

    public static void Add(int promptTokens, int completionTokens)
    {
        if (Current.Value is { } state)
            Interlocked.Add(ref state.Used, promptTokens + completionTokens);
    }

    /// <summary>下一次模型调用前调用；已超预算则抛出。</summary>
    public static void EnsureWithinBudget()
    {
        if (Current.Value is { } state && Volatile.Read(ref state.Used) >= state.Limit)
            throw new TokenBudgetExceededException(state.Limit, Volatile.Read(ref state.Used));
    }
}
