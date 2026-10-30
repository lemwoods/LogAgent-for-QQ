using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 带并发探测的模型桩：记录同时进行的调用数峰值，用来验证「同群串行 / 跨群并行」。
/// </summary>
internal sealed class GatedClient(string providerId, TimeSpan delay) : IChatModelClient
{
    private int _current;
    private int _max;
    private int _calls;

    public string ProviderId { get; } = providerId;

    public int Calls => Volatile.Read(ref _calls);
    public int MaxConcurrent => Volatile.Read(ref _max);

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        var running = Interlocked.Increment(ref _current);
        InterlockedMax(ref _max, running);
        try
        {
            await Task.Delay(delay, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
        return new ChatResult("测试回复", ProviderId, "gated", 1, 1, delay);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        while (value > (snapshot = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, snapshot);
    }
}

/// <summary>
/// 并发边界：三个宿主的消息事件都是 async void，同群消息会并发进入 AgentRuntime。
/// 这里验证每个 chat 一把门之后的行为（对应 docs/09 的 D1/D2/D3）。
/// </summary>
public class ConcurrencyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-cc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private static AppConfig Config() => new()
    {
        Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
        Rules =
        [
            new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] },
            new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
            new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
            new RouterRule { TaskType = TaskType.Summary, ProviderChain = ["p1"] },
        ],
    };

    private (AgentRuntime Runtime, GatedClient Client, Database Db) Make(TimeSpan delay)
    {
        var appConfig = Config();
        var client = new GatedClient("p1", delay);
        var router = new ModelRouter(appConfig, _ => client);
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, new AgentConfig());
        var runtime = new AgentRuntime(
            new GroupContextAgent(router),
            new LogAnalysisAgent(router),
            learningAgent: null,
            sessions,
            new AgentConfig());
        return (runtime, client, db);
    }

    private static BotMessage Msg(string text, string chatId = "c1") => new()
    {
        MessageId = $"m-{Guid.NewGuid():N}",
        ChatId = chatId,
        ChatType = ChatType.Group,
        SenderId = "1",
        SenderName = "柠枺",
        Text = text,
        MentionedMe = true,
    };

    private static int CountSessions(string dbPath, string chatId)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE chat_id = $c";
        cmd.Parameters.AddWithValue("$c", chatId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task SameChat_IsSerialized()
    {
        var (runtime, client, _) = Make(TimeSpan.FromMilliseconds(25));
        var bridge = new RecordingBridge();

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => runtime.OnMessageAsync(bridge, Msg($"@机器人 第{i}条"))));

        Assert.Equal(8, client.Calls);
        Assert.Equal(1, client.MaxConcurrent);   // 同群任意时刻只有一个在跑
        Assert.Equal(8, bridge.Sent.Count);
    }

    [Fact]
    public async Task DifferentChats_RunInParallel()
    {
        var (runtime, client, _) = Make(TimeSpan.FromMilliseconds(60));
        var bridge = new RecordingBridge();

        await Task.WhenAll(
            runtime.OnMessageAsync(bridge, Msg("@机器人 群A", "cA")),
            runtime.OnMessageAsync(bridge, Msg("@机器人 群B", "cB")));

        Assert.True(client.MaxConcurrent >= 2, $"期望跨群并行，实际峰值 {client.MaxConcurrent}");
        Assert.Equal(2, bridge.Sent.Count);
    }

    [Fact]
    public async Task ConcurrentFirstMessages_CreateSingleSessionRow()
    {
        var (runtime, _, _) = Make(TimeSpan.FromMilliseconds(10));
        var bridge = new RecordingBridge();

        await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(i => runtime.OnMessageAsync(bridge, Msg($"@机器人 首条{i}", "c-new"))));

        Assert.Equal(1, CountSessions(_dbPath, "c-new"));
    }

    /// <summary>
    /// 直接压 SessionManager（绕过 runtime 的门）：并发首消息也只会建一行会话。
    /// 门只保护走 AgentRuntime 的路径，停机切段（CutAllSessionsAsync）仍会直接调它。
    /// </summary>
    [Fact]
    public async Task SessionManager_ConcurrentFirstMessages_CreateSingleSessionRow()
    {
        var appConfig = Config();
        var router = new ModelRouter(appConfig, _ => new GatedClient("p1", TimeSpan.FromMilliseconds(5)));
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var manager = new SessionManager(new SessionStore(db), router, new AgentConfig());

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => manager.OnIncomingAsync(new BotMessage
            {
                MessageId = $"m-{i}",
                ChatId = "c-direct",
                ChatType = ChatType.Group,
                SenderId = "1",
                SenderName = "柠枺",
                Text = $"消息{i}",
            })));

        Assert.Equal(1, CountSessions(_dbPath, "c-direct"));
    }

    [Fact]
    public async Task WaitingMessage_IsDroppedWhenCancelled()
    {
        var (runtime, _, _) = Make(TimeSpan.FromMilliseconds(120));
        var bridge = new RecordingBridge();
        using var cts = new CancellationTokenSource();

        var first = runtime.OnMessageAsync(bridge, Msg("@机器人 第一条"));
        var second = runtime.OnMessageAsync(bridge, Msg("@机器人 第二条"), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await first;
        await second;   // 等待中被取消：正常返回，不抛

        Assert.Single(bridge.Sent);   // 只有第一条被处理
    }
}
