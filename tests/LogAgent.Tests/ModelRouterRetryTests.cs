using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

/// <summary>按脚本返回成功/失败的桩：用于验证重试与超时切换。失败抛 TaskCanceledException（模拟 HttpClient 超时）。</summary>
internal sealed class ScriptedClient(string providerId, params bool[] outcomes) : IChatModelClient
{
    private int _calls;

    public string ProviderId { get; } = providerId;
    public int Calls => Volatile.Read(ref _calls);

    public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref _calls);   // 先计数：调用方取消时也要能看出「进入过一次」
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

        var ok = n <= outcomes.Length ? outcomes[n - 1] : outcomes[^1];
        if (!ok) throw new TaskCanceledException($"simulated timeout #{n}");
        return Task.FromResult(new ChatResult("ok", providerId, "scripted", 1, 1, TimeSpan.Zero));
    }
}

/// <summary>
/// 路由重试语义（docs/09 §6.7）：
/// · 超时（HttpClient 的 TaskCanceledException）算失败，不再直接冒泡；
/// · 同一 provider 内重试一次，用尽后才切下一个；
/// · 调用方取消时立即停，不重试也不切换。
/// </summary>
public class ModelRouterRetryTests
{
    private static AppConfig Config(int attempts, params string[] chain) => new()
    {
        Providers =
        [
            new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m1", Enabled = true },
            new ProviderConfig { Id = "p2", Name = "P2", ChatModel = "m2", Enabled = true },
        ],
        Rules = [new RouterRule { TaskType = TaskType.Chat, ProviderChain = [.. chain] }],
        ProviderMaxAttempts = attempts,
    };

    private static ModelRouter Router(AppConfig config, Dictionary<string, ScriptedClient> clients)
        => new(config, p => clients[p.Id]);

    [Fact]
    public async Task Timeout_CountsAsFailure_AndFailsOver()
    {
        var config = Config(attempts: 1, "p1", "p2");
        var clients = new Dictionary<string, ScriptedClient>
        {
            ["p1"] = new("p1", false),
            ["p2"] = new("p2", true),
        };

        var result = await Router(config, clients).CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")]);

        Assert.Equal("p2", result.ProviderId);
        Assert.Equal(1, clients["p1"].Calls);
        Assert.Equal(1, clients["p2"].Calls);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedWithinSameProvider()
    {
        var config = Config(attempts: 2, "p1", "p2");
        var clients = new Dictionary<string, ScriptedClient>
        {
            ["p1"] = new("p1", false, true),
            ["p2"] = new("p2", true),
        };

        var result = await Router(config, clients).CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")]);

        Assert.Equal("p1", result.ProviderId);
        Assert.Equal(2, clients["p1"].Calls);
        Assert.Equal(0, clients["p2"].Calls);
    }

    [Fact]
    public async Task RetryExhausted_ThenFailsOver()
    {
        var config = Config(attempts: 2, "p1", "p2");
        var clients = new Dictionary<string, ScriptedClient>
        {
            ["p1"] = new("p1", false, false),
            ["p2"] = new("p2", true),
        };

        var result = await Router(config, clients).CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")]);

        Assert.Equal("p2", result.ProviderId);
        Assert.Equal(2, clients["p1"].Calls);
        Assert.Equal(1, clients["p2"].Calls);
    }

    [Fact]
    public async Task CallerCancellation_StopsImmediately()
    {
        var config = Config(attempts: 3, "p1", "p2");
        var clients = new Dictionary<string, ScriptedClient>
        {
            ["p1"] = new("p1", true),
            ["p2"] = new("p2", true),
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Router(config, clients).CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")], ct: cts.Token));

        Assert.Equal(1, clients["p1"].Calls);   // 只进入一次，未重试
        Assert.Equal(0, clients["p2"].Calls);   // 未切换到下一个 provider
    }

    [Fact]
    public async Task Metered_FiresOnEveryAttempt()
    {
        var config = Config(attempts: 2, "p1");
        var clients = new Dictionary<string, ScriptedClient> { ["p1"] = new("p1", false, true) };
        var router = Router(config, clients);
        var events = new List<RouterMeterEvent>();
        router.Metered += (_, e) => events.Add(e);

        await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("hi")]);

        Assert.Equal(2, events.Count);
        Assert.False(events[0].Success);
        Assert.True(events[1].Success);
    }
}
