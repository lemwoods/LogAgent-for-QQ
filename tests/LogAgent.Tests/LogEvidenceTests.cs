using LogAgent.Core;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 证据可追溯性：README / AGENTS.md 承诺「带行号证据的根因报告」。
/// 粘贴日志（文本模式）此前在聚类这一步把行号丢掉了，这里锁住修复后的行为，
/// 同时作为 docs/09 评估集（Tier 1）的第一批结构断言。
/// </summary>
public class LogEvidenceTests
{
    private const string Log = """
        [12:00:01] [main/INFO]: Starting minecraft server
        [12:00:02] [main/ERROR]: java.lang.NoClassDefFoundError: com/example/Mod
        [12:00:03] [main/INFO]: Loading 42 mods
        [12:00:04] [main/ERROR]: java.lang.NoClassDefFoundError: com/example/Mod
        """;

    [Fact]
    public void ErrorCluster_CarriesLineNumbers()
    {
        var stats = LogStats.Compute(LogParser.Parse(Log));

        var cluster = Assert.Single(stats.TopErrors);
        Assert.Equal(2, cluster.Count);
        Assert.Equal(2, cluster.FirstLineNo);
        Assert.All(cluster.Samples, s => Assert.StartsWith("[L", s));
        Assert.Contains("[L2]", cluster.Samples[0]);
    }

    [Fact]
    public async Task AnalyzeText_SendsLineNumbersToModel()
    {
        var inputs = new List<string>();
        var config = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules =
            [
                new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
            ],
        };
        var router = new ModelRouter(config, _ => new FakeClient("p1", true)
        {
            OnInput = input => { lock (inputs) inputs.Add(input); },
        });

        var reply = await new LogAnalysisAgent(router).AnalyzeAsync(Log);

        Assert.Contains("首行 L2", string.Concat(inputs));
        Assert.Contains("[L2]", string.Concat(inputs));
        Assert.Contains("日志分析报告", reply);
    }

    /// <summary>模型返回空正文（推理吃光预算）时，不能把它当正常结论——必须给出可操作的说明。</summary>
    [Fact]
    public async Task EmptyModelReply_ProducesActionableReport()
    {
        var config = new AppConfig
        {
            Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules =
            [
                new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = ["p1"] },
                new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = ["p1"] },
            ],
        };
        var router = new ModelRouter(config, _ => new FakeClient("p1", true, reply: ""));

        var reply = await new LogAnalysisAgent(router).AnalyzeAsync(Log);

        Assert.Contains("未能生成结论", reply);
        Assert.Contains("MaxTokens", reply);
    }
}
