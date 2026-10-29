using LogAgent.Core;
using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using LogAgent.Skills;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 知识库检索工具（docs/09 §6.9）：没有嵌入模型时，把「换关键词再查」交给模型自己决定。
/// 覆盖：工具暴露 / 参数校验 / top_k 夹取 / 文本模式下的工具循环。
/// </summary>
public class KnowledgeToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"la-hub-{Guid.NewGuid():N}");

    public KnowledgeToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ToolHostHub MakeHub(Func<string, int, CancellationToken, Task<string>>? search)
    {
        var builtin = Path.Combine(_root, "builtin");
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(builtin);
        Directory.CreateDirectory(user);
        var registry = new SkillRegistry(builtin, user);
        registry.Reload();
        return new ToolHostHub(registry, null, new AppConfig(), knowledgeSearch: search);
    }

    [Fact]
    public async Task SearchKnowledge_IsExposed_AndClampsTopK()
    {
        string? seenQuery = null;
        var seenTopK = 0;
        var hub = MakeHub((query, topK, _) =>
        {
            seenQuery = query;
            seenTopK = topK;
            return Task.FromResult($"[K1] 命中：{query}");
        });

        Assert.Contains(hub.BuildToolDefinitions(new SkillsConfig()), d => d.Name == ToolHostHub.SearchKnowledgeTool);

        var result = await hub.ExecuteToolAsync(
            ToolHostHub.SearchKnowledgeTool, """{"query":"SIGSEGV","top_k":9}""", new SkillsConfig());

        Assert.Contains("命中：SIGSEGV", result);
        Assert.Equal("SIGSEGV", seenQuery);
        Assert.Equal(5, seenTopK); // 9 被夹到上限 5
    }

    [Fact]
    public async Task SearchKnowledge_WithoutDelegate_IsHiddenAndRejects()
    {
        var hub = MakeHub(null);

        Assert.DoesNotContain(hub.BuildToolDefinitions(new SkillsConfig()), d => d.Name == ToolHostHub.SearchKnowledgeTool);
        var result = await hub.ExecuteToolAsync(
            ToolHostHub.SearchKnowledgeTool, """{"query":"x"}""", new SkillsConfig());

        Assert.Contains("未启用", result);
    }

    [Fact]
    public async Task SearchKnowledge_BadArguments_ReturnErrorText()
    {
        var hub = MakeHub((q, _, _) => Task.FromResult("ok"));

        Assert.Contains("缺少必填参数", await hub.ExecuteToolAsync(
            ToolHostHub.SearchKnowledgeTool, """{}""", new SkillsConfig()));
        Assert.Contains("不是合法 JSON", await hub.ExecuteToolAsync(
            ToolHostHub.SearchKnowledgeTool, """{"query":""", new SkillsConfig()));
    }

    [Fact]
    public async Task ToolException_BecomesErrorText_NotCrash()
    {
        var hub = MakeHub((_, _, _) => Task.FromException<string>(new InvalidOperationException("boom")));

        var result = await hub.ExecuteToolAsync(
            ToolHostHub.SearchKnowledgeTool, """{"query":"x"}""", new SkillsConfig());

        Assert.Contains("执行失败", result);
        Assert.Contains("boom", result);
    }

    [Fact]
    public async Task TextMode_ModelCanSearchKnowledge_ThenAnswer()
    {
        var queries = new List<string>();
        var hub = MakeHub((query, _, _) =>
        {
            queries.Add(query);
            return Task.FromResult("[K1] 内存不足案例：OutOfMemoryError → 调低 -Xmx");
        });

        var config = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules =
            [
                new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
            ],
            ProviderMaxAttempts = 1,
        };

        var calls = 0;
        var router = new ModelRouter(config, _ => new FakeClient("p1", true)
        {
            ResultOverride = request =>
            {
                var all = string.Join("\n", request.Messages.Select(m => m.Content));
                if (all.Contains("严格的技术评审", StringComparison.Ordinal))
                    return new ChatResult("PASS 合理", "p1", "m", 1, 1, TimeSpan.Zero);

                calls++;
                return calls == 1
                    ? new ChatResult("", "p1", "m", 1, 1, TimeSpan.Zero,
                        [new ChatToolCall("c1", ToolHostHub.SearchKnowledgeTool, """{"query":"OutOfMemoryError"}""")])
                    : new ChatResult("【结论】内存不足", "p1", "m", 1, 1, TimeSpan.Zero);
            },
        });

        var agent = new LogAnalysisAgent(router, skills: hub, skillsConfig: new SkillsConfig());
        var reply = await agent.AnalyzeAsync("""
            [10:03:01] [main/INFO]: Starting
            [10:03:02] [main/ERROR]: java.lang.OutOfMemoryError: Java heap space
            """);

        Assert.Equal(["OutOfMemoryError"], queries);
        Assert.Contains("内存不足", reply);
    }
}
