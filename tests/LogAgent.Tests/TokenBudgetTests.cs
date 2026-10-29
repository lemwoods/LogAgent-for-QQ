using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 单条消息的 token 预算（docs/09 §6.12）：超限中止、不降级、不重试，
/// 并给用户明确提示。预算按消息隔离（AsyncLocal），消息结束即清零。
/// </summary>
public class TokenBudgetTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-budget-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TokenBudget.End();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private static AppConfig Config() => new()
    {
        Providers =
        [
            new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m1", Enabled = true },
            new ProviderConfig { Id = "p2", Name = "P2", ChatModel = "m2", Enabled = true },
        ],
        Rules =
        [
            new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1", "p2"] },
            new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1", "p2"] },
            new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1", "p2"] },
        ],
        ProviderMaxAttempts = 2,
    };

    [Fact]
    public async Task ExceededBudget_AbortsWithoutRetryOrFailover()
    {
        var p1 = new FakeClient("p1", true)
        {
            ResultOverride = _ => new ChatResult("ok", "p1", "m", 40, 40, TimeSpan.Zero),
        };
        var p2 = new FakeClient("p2", true);
        var router = new ModelRouter(Config(), p => p.Id == "p1" ? p1 : p2);

        TokenBudget.Begin(50);
        await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("a")]); // 用掉 80
        await Assert.ThrowsAsync<TokenBudgetExceededException>(
            () => router.CompleteAsync(TaskType.Chat, [ChatTurn.User("b")]));

        Assert.Equal(1, p1.Calls); // 没有重试
        Assert.Equal(0, p2.Calls); // 没有切换 provider
    }

    [Fact]
    public async Task Budget_IsScopedToMessage()
    {
        var p1 = new FakeClient("p1", true)
        {
            ResultOverride = _ => new ChatResult("ok", "p1", "m", 40, 40, TimeSpan.Zero),
        };
        var router = new ModelRouter(Config(), _ => p1);

        TokenBudget.Begin(50);
        await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("a")]);
        TokenBudget.End();

        // 新消息：预算重新计数，不应再抛
        TokenBudget.Begin(50);
        var result = await router.CompleteAsync(TaskType.Chat, [ChatTurn.User("b")]);
        Assert.Equal("ok", result.Content);
    }

    [Fact]
    public async Task AgentRuntime_ExceededBudget_NotifiesUser()
    {
        var appConfig = Config();
        var client = new FakeClient("p1", true)
        {
            // 每次调用都返回 200 tokens，第一条消息的第二次调用就会超预算
            ResultOverride = _ => new ChatResult("分析结论", "p1", "m", 100, 100, TimeSpan.Zero),
        };
        var router = new ModelRouter(appConfig, _ => client);
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, new AgentConfig());
        var runtime = new AgentRuntime(
            new GroupContextAgent(router),
            new LogAnalysisAgent(router),
            learningAgent: null,
            sessions,
            new AgentConfig { MessageMaxTokens = 50, MessageTimeoutSeconds = 30 });
        var bridge = new RecordingBridge();

        await runtime.OnMessageAsync(bridge, new BotMessage
        {
            MessageId = "m1",
            ChatId = "c1",
            ChatType = ChatType.Group,
            SenderId = "1",
            SenderName = "柠枺",
            Text = "@机器人 分析日志\n[1] ERROR: boom\n[2] INFO: x\n[3] INFO: y",
            MentionedMe = true,
        });

        var sent = Assert.Single(bridge.Sent);
        Assert.Contains("预算", sent.Text);
        Assert.Equal(0, TokenBudget.Used); // 消息结束即清零
    }
}
