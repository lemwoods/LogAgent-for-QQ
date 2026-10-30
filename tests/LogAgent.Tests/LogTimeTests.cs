using LogAgent.Core;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 时间窗切片（docs/08 P2）：从行内时间戳算日志时间范围与错误按分钟分布，
/// 结果进入模型材料，让「什么时候开始报错、集中在哪几分钟」有据可判。
/// </summary>
public class LogTimeTests
{
    [Fact]
    public void ClockPrefix_ComputesRangeAndBuckets()
    {
        var stats = LogStats.Compute(LogParser.Parse("""
            [10:00:01] [main/INFO]: Starting
            [10:03:02] [main/ERROR]: boom
            [10:03:03] [main/ERROR]: boom again
            [10:05:04] [main/INFO]: Done
            """));

        var time = Assert.IsType<LogStats.TimeSlice>(stats.Time);
        Assert.Equal(new TimeOnly(10, 0, 1), time.First);
        Assert.Equal(new TimeOnly(10, 5, 4), time.Last);

        var bucket = Assert.Single(time.ErrorBuckets);
        Assert.Equal("10:03", bucket.Minute);
        Assert.Equal(2, bucket.Count);
    }

    [Fact]
    public void DatePrefix_AlsoParsed()
    {
        var stats = LogStats.Compute(LogParser.Parse("""
            2026-10-30 10:00:01 ERROR first
            2026-10-30 10:01:02 INFO second
            """));

        Assert.Equal(new TimeOnly(10, 0, 1), stats.Time!.First);
        Assert.Equal(new TimeOnly(10, 1, 2), stats.Time.Last);
    }

    [Fact]
    public void NoTimestamp_TimeIsNull()
    {
        var stats = LogStats.Compute(LogParser.Parse("ERROR boom\nINFO ok\n"));

        Assert.Null(stats.Time);
    }

    [Fact]
    public async Task TimeRange_IsIncludedInModelMaterial()
    {
        // 材料由 BuildMaterial 生成（私有），这里通过 AnalyzeTextAsync 的输入间接验证
        var inputs = new List<string>();
        var config = new LogAgent.Shared.Configuration.AppConfig
        {
            Providers = [new LogAgent.Shared.Configuration.ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
            Rules =
            [
                new LogAgent.Shared.Configuration.RouterRule { TaskType = LogAgent.Shared.Configuration.TaskType.LogAnalysis, ProviderChain = ["p1"] },
                new LogAgent.Shared.Configuration.RouterRule { TaskType = LogAgent.Shared.Configuration.TaskType.DeepReasoning, ProviderChain = ["p1"] },
            ],
            ProviderMaxAttempts = 1,
        };
        var router = new LogAgent.Routing.ModelRouter(config, _ => new FakeClient("p1", true)
        {
            OnInput = input => { lock (inputs) inputs.Add(input); },
        });

        await new LogAnalysisAgent(router).AnalyzeAsync("""
            [10:00:01] [main/ERROR]: boom
            [10:01:02] [main/INFO]: ok
            """);

        Assert.Contains("时间范围：10:00:01–10:01:02", string.Concat(inputs));
    }
}
