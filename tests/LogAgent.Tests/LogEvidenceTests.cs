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

    /// <summary>
    /// 没有 ERROR 级别的日志也不能由代码判「运行正常」：原文要进材料，结论由模型给。
    /// （生产日志里出现过「看起来运行正常 ✅」答非所问，随后模型自己道歉。）
    /// </summary>
    [Fact]
    public async Task InfoOnlyLog_IsJudgedByModel_NotByHardcodedVerdict()
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
            ProviderMaxAttempts = 1,
        };
        var router = new ModelRouter(config, _ => new FakeClient("p1", true, "没有 ERROR，但有 2 条 WARN 建议关注")
        {
            OnInput = input => { lock (inputs) inputs.Add(input); },
        });

        var reply = await new LogAnalysisAgent(router).AnalyzeAsync("""
            [10:00:01] [main/INFO]: Loading 12 mods
            [10:00:02] [main/WARN]: Can't keep up! Is the server overloaded?
            [10:00:03] [main/INFO]: Done
            """);

        Assert.Contains("WARN", reply);
        Assert.DoesNotContain("看起来运行正常", reply);
        Assert.Contains("Can't keep up", string.Concat(inputs)); // 原文进了材料，模型有据可判
    }

    /// <summary>模型返回空正文（推理吃光预算）时，不能把它当正常结论——必须给出可操作的说明。</summary>    [Fact]
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
