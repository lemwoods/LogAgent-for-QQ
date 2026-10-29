using System.Diagnostics;
using System.Net;
using System.Net.Http;
using LogAgent.Core;
using LogAgent.Memory;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Shared.Messaging;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 富化的阻塞边界：链接抓取在会话门内执行，外链卡住会连带阻塞该群后续消息。
/// 这里锁住「链接有独立预算、超时后消息照常回复」这一行为（docs/09 §1.4 风险点）。
/// </summary>
public class EnrichBudgetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"la-enrich-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"la-enrich-{Guid.NewGuid():N}.db");

    public EnrichBudgetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        }
    }

    private (AgentRuntime Runtime, RecordingBridge Bridge) Make(
        MessageEnricher enricher, List<string> inputs, TimeSpan budget)
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
        };
        var router = new ModelRouter(config, _ => new FakeClient("p1", true)
        {
            OnInput = input => { lock (inputs) inputs.Add(input); },
        });
        var db = new Database(_dbPath);
        db.EnsureCreated();
        var sessions = new SessionManager(new SessionStore(db), router, new AgentConfig());
        var runtime = new AgentRuntime(
            new GroupContextAgent(router),
            new LogAnalysisAgent(router),
            learningAgent: null,
            sessions,
            new AgentConfig(),
            enricher,
            budget);
        return (runtime, new RecordingBridge());
    }

    private static BotMessage Msg(string text) => new()
    {
        MessageId = "m1",
        ChatId = "c1",
        ChatType = ChatType.Group,
        SenderId = "1",
        SenderName = "柠枺",
        Text = text,
        MentionedMe = true,
    };

    [Fact]
    public async Task HangingLink_IsBounded_AndMessageStillAnswered()
    {
        var inputs = new List<string>();
        using var http = new HttpClient(new HangingHandler());
        var enricher = new MessageEnricher(http: http, filesDir: _dir);
        var (runtime, bridge) = Make(enricher, inputs, TimeSpan.FromMilliseconds(150));

        var sw = Stopwatch.StartNew();
        await runtime.OnMessageAsync(bridge, Msg("@机器人 看看这个 https://logshare.cn/abc12345"));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"富化未被预算截断，耗时 {sw.Elapsed}");
        Assert.Contains("链接抓取超时", string.Concat(inputs));
        Assert.Single(bridge.Sent);
    }
}
