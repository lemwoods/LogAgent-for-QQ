using System.Diagnostics;
using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 单条消息的处理时限（docs/08 P1）：模型长时间不返回时中止该条，并给用户明确提示，
/// 而不是让该群一直被占着、最后只回一句内部错误。
/// </summary>
public class MessageTimeoutTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-timeout-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task SlowModel_HitsDeadline_AndUserGetsNotice()
    {
        var config = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules =
            [
                new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
            ],
            ProviderMaxAttempts = 1,
        };
        var router = new ModelRouter(config, _ => new GatedClient("p1", TimeSpan.FromSeconds(5)));
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, new AgentConfig());
        var runtime = new AgentRuntime(
            new GroupContextAgent(router),
            new LogAnalysisAgent(router),
            learningAgent: null,
            sessions,
            new AgentConfig { MessageTimeoutSeconds = 1 });
        var bridge = new RecordingBridge();

        var sw = Stopwatch.StartNew();
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
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"未按时中止，耗时 {sw.Elapsed}");
        var sent = Assert.Single(bridge.Sent);
        Assert.Contains("超过", sent.Text);
    }
}
