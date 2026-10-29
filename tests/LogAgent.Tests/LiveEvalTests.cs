using LogAgent.Core;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// Tier 2 真模型评估的开关：只有显式指定 provider（或端点）才运行。
/// 默认跳过——CI 没有密钥，也不该让「改一句提示词」自动产生账单。
/// </summary>
public sealed class LiveEvalFactAttribute : FactAttribute
{
    public const string ProviderVar = "LOGAGENT_EVAL_PROVIDER";
    public const string ModelVar = "LOGAGENT_EVAL_MODEL";

    public LiveEvalFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProviderVar)) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelVar)))
            Skip = $"未设置 {ProviderVar} / {ModelVar}（Tier 2 走真模型，默认跳过）";
    }
}

/// <summary>
/// Tier 2 真模型评估（docs/09 §2.2）：跑真实 provider，验「根因是否说到点上」+「有没有编造日志里不存在的东西」。
///
/// 两种用法：
/// <code>
/// # 1) 用本机配置里已有的 provider
/// LOGAGENT_EVAL_PROVIDER=&lt;providerId&gt; dotnet test --filter FullyQualifiedName~LiveEvalTests
///
/// # 2) 直接指端点（生产模型 glm-5.3-flash 走本地网关时）
/// LOGAGENT_EVAL_MODEL=glm-5.3-flash \
/// LOGAGENT_EVAL_BASE_URL=http://127.0.0.1:8765/v1 \
/// LOGAGENT_EVAL_KEY_FILE=~/.config/opencode/workbuddy.key \
///   dotnet test --filter FullyQualifiedName~LiveEvalTests
/// </code>
/// </summary>
[Collection("config-dir-env")]
public class LiveEvalTests
{
    [LiveEvalFact]
    public async Task Fixtures_ProduceAcceptableRootCause_WithoutHallucinating()
    {
        var config = AppConfigStore.Load();
        var provider = ResolveProvider(config);

        config.Rules.RemoveAll(r => r.TaskType is TaskType.LogAnalysis or TaskType.DeepReasoning);
        config.Rules.Add(new RouterRule { TaskType = TaskType.LogAnalysis, ProviderChain = [provider.Id] });
        config.Rules.Add(new RouterRule { TaskType = TaskType.DeepReasoning, ProviderChain = [provider.Id] });

        var agent = new LogAnalysisAgent(new ModelRouter(config));
        var failures = new List<string>();

        foreach (var evalCase in LogEvalCases.All)
        {
            var reply = await agent.AnalyzeAsync(evalCase.Log);

            if (!evalCase.AcceptAny.Any(k => reply.Contains(k, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"{evalCase.Name}：未命中任何可接受根因（{string.Join(" / ", evalCase.AcceptAny)}）\n{reply}");

            if (reply.Contains(evalCase.Decoy, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{evalCase.Name}：报告提到日志里不存在的「{evalCase.Decoy}」，疑似幻觉\n{reply}");
        }

        var passed = LogEvalCases.All.Length - failures.Count;
        Assert.True(failures.Count == 0,
            $"通过率 {(double)passed / LogEvalCases.All.Length:P0}（{passed}/{LogEvalCases.All.Length}）\n\n" +
            string.Join("\n\n", failures));
    }

    /// <summary>解析评测用 provider：优先环境变量直指端点，否则回退到本机配置里的 provider id。</summary>
    private static ProviderConfig ResolveProvider(AppConfig config)
    {
        var endpoint = Environment.GetEnvironmentVariable("LOGAGENT_EVAL_BASE_URL")?.Trim();
        var model = Environment.GetEnvironmentVariable(LiveEvalFactAttribute.ModelVar)?.Trim();
        var providerId = Environment.GetEnvironmentVariable(LiveEvalFactAttribute.ProviderVar)?.Trim();

        if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(model))
        {
            var provider = new ProviderConfig
            {
                Id = string.IsNullOrEmpty(providerId) ? "live-eval" : providerId,
                Name = "LiveEval",
                Endpoint = endpoint,
                ApiKey = ReadApiKey(),
                ChatModel = model,
                Enabled = true,
            };
            config.Providers.Add(provider);
            return provider;
        }

        var existing = config.Providers.FirstOrDefault(p => p.Id == providerId);
        Assert.True(existing is not null,
            $"配置里找不到 provider「{providerId}」（{AppConfigStore.GetConfigPath()}）；" +
            $"也可以改用 {LiveEvalFactAttribute.ModelVar} + LOGAGENT_EVAL_BASE_URL 直接指端点");
        Assert.True(existing!.Enabled, $"provider「{providerId}」处于禁用状态");
        return existing;
    }

    private static string ReadApiKey()
    {
        var inline = Environment.GetEnvironmentVariable("LOGAGENT_EVAL_API_KEY");
        if (!string.IsNullOrWhiteSpace(inline)) return inline.Trim();

        var keyFile = Environment.GetEnvironmentVariable("LOGAGENT_EVAL_KEY_FILE");
        if (string.IsNullOrWhiteSpace(keyFile)) return "";

        var path = keyFile.Trim();
        if (path.StartsWith("~/", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
    }
}
