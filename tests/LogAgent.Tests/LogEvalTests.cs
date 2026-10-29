using LogAgent.Core;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// Tier 1 离线评估（docs/09 §2.2）：不调真模型，只验「关键信号有没有活着进入模型材料」。
/// 盯的是 <see cref="LogStats"/> 聚类与 <see cref="LogAnalysisAgent"/> 降采样（6000 字符 / 错误簇 6 / 堆栈 3）这段最易回归的链路。
///
/// 断言口径：证据锚点必须出现在材料里且带行号；不做全文匹配，避免改一句提示词就红。
/// 样例见 <see cref="LogEvalCases"/>（与 Tier 2 共用）。
/// </summary>
public class LogEvalTests
{
    private static (ModelRouter Router, List<string> Inputs) MakeRouter()
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
        return (router, inputs);
    }

    [Fact]
    public async Task AllCases_KeepAnchorAndLineNumbersInMaterial()
    {
        var failures = new List<string>();
        var (router, inputs) = MakeRouter();

        foreach (var evalCase in LogEvalCases.All)
        {
            inputs.Clear();
            await new LogAnalysisAgent(router).AnalyzeAsync(evalCase.Log);

            var material = string.Concat(inputs);
            if (!material.Contains(evalCase.Anchor, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{evalCase.Name}：材料里缺少锚点「{evalCase.Anchor}」");
            if (evalCase.AnchorLine > 0 && !material.Contains($"[L{evalCase.AnchorLine}]", StringComparison.Ordinal))
                failures.Add($"{evalCase.Name}：材料里没有锚点所在行号 [L{evalCase.AnchorLine}]");
            if (!material.Contains("[L", StringComparison.Ordinal))
                failures.Add($"{evalCase.Name}：材料里完全没有行号");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>统计层面的口径：每条样例都必须被识别为「有错误」的日志，不能走「看起来运行正常」分支。</summary>
    [Fact]
    public void AllCases_AreRecognizedAsErrorLogs()
    {
        foreach (var evalCase in LogEvalCases.All)
        {
            var stats = LogStats.Compute(LogParser.Parse(evalCase.Log));
            Assert.True(stats.ErrorLike > 0, $"{evalCase.Name}：ErrorLike=0，会被判定为正常日志");
            Assert.NotEmpty(stats.TopErrors);
        }
    }
}
