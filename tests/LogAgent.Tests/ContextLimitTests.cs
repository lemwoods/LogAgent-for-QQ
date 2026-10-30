using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 上下文上限与压缩（docs/09 §6.16）：
/// 窗口保留 50 轮；模型报上下文超长时压缩会话并重试，而不是回「内部错误」。
/// </summary>
public class ContextLimitTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-ctx-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    private static AppConfig Config() => new()
    {
        Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
        Rules =
        [
            new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] },
            new RouterRule { TaskType = TaskType.Summary, ProviderChain = ["p1"] },
            new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
            new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
        ],
        ProviderMaxAttempts = 1, // 让 provider 错误直接冒泡，便于验证 AgentRuntime 的压缩重试
    };

    [Fact]
    public async Task ContextWindow_Keeps50Turns()
    {
        var router = new ModelRouter(Config(), _ => new FakeClient("p1", true));
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var manager = new SessionManager(new SessionStore(db), router,
            new AgentConfig { ContextWindowTurns = 50, SessionMaxMessages = 500 });

        SessionContext? last = null;
        for (var i = 0; i < 60; i++)
        {
            last = await manager.OnIncomingAsync(new BotMessage
            {
                MessageId = $"m{i}",
                ChatId = "c1",
                ChatType = ChatType.Group,
                SenderId = "1",
                SenderName = "柠枺",
                Text = $"第 {i} 条",
            });
        }

        Assert.NotNull(last);
        Assert.Equal(50, last!.WindowTurns.Count);
    }

    [Fact]
    public async Task ContextOverflow_CompressesAndRetries()
    {
        var calls = 0;
        var client = new FakeClient("p1", true)
        {
            ResultOverride = request =>
            {
                var input = string.Join("\n", request.Messages.Select(m => m.Content));
                if (input.Contains("会话摘要器", StringComparison.Ordinal))
                    return new ChatResult("压缩后的摘要", "p1", "m", 1, 1, TimeSpan.Zero);

                calls++;
                if (calls == 1)
                    throw new InvalidOperationException("maximum context length exceeded");
                return new ChatResult("压缩后重试成功", "p1", "m", 1, 1, TimeSpan.Zero);
            },
        };
        var router = new ModelRouter(Config(), _ => client);
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, new AgentConfig());
        var runtime = new AgentRuntime(
            new GroupContextAgent(router),
            new LogAnalysisAgent(router),
            learningAgent: null,
            sessions,
            new AgentConfig { MessageTimeoutSeconds = 30 });
        var bridge = new RecordingBridge();

        await runtime.OnMessageAsync(bridge, new BotMessage
        {
            MessageId = "m1",
            ChatId = "c1",
            ChatType = ChatType.Group,
            SenderId = "1",
            SenderName = "柠枺",
            Text = "@机器人 你好",
            MentionedMe = true,
        });

        var sent = Assert.Single(bridge.Sent);
        Assert.Contains("压缩后重试成功", sent.Text);
        Assert.DoesNotContain("打个嗝", sent.Text);
        Assert.Equal(2, calls); // 首次失败 + 压缩后重试
    }
}
