using LogAgent.Core;
using LogAgent.Routing;
using LogAgent.Shared.Configuration;
using LogAgent.Skills;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// run_shell 的 AI 审核（docs/09 §6.13）：审核放行才执行；拒绝/审核失败一律挡下。
/// 沙箱白名单仍是第一道防线，这里是第二道（组合用法、越权、与排障无关）。
/// </summary>
public class ShellReviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"la-review-{Guid.NewGuid():N}");

    public ShellReviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static AppConfig Config() => new()
    {
        Providers = [new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m", Enabled = true }],
        Rules = [new RouterRule { TaskType = TaskType.Chat, ProviderChain = ["p1"] }],
        ProviderMaxAttempts = 2,
    };

    private static ModelRouter Router(string reply, bool succeed = true)
        => new(Config(), _ => new FakeClient("p1", succeed, reply));

    [Fact]
    public async Task AllowVerdict_ParsesReason()
    {
        var verdict = await new ShellCommandReviewer(Router("ALLOW 只读检索")).ReviewAsync("grep -n error app.log");

        Assert.True(verdict.Allow);
        Assert.Contains("只读", verdict.Reason);
    }

    [Fact]
    public async Task DenyVerdict_Blocks()
    {
        var verdict = await new ShellCommandReviewer(Router("DENY 会修改系统配置")).ReviewAsync("systemctl stop nginx");

        Assert.False(verdict.Allow);
        Assert.Contains("修改系统配置", verdict.Reason);
    }

    [Fact]
    public async Task ReviewFailure_DeniesByDefault()
    {
        var verdict = await new ShellCommandReviewer(Router("ok", succeed: false)).ReviewAsync("ls");

        Assert.False(verdict.Allow);
        Assert.Contains("审核不可用", verdict.Reason);
    }

    [Fact]
    public async Task Hub_WhenReviewDenies_DoesNotRunShell()
    {
        var builtin = Path.Combine(_dir, "builtin");
        var user = Path.Combine(_dir, "user");
        Directory.CreateDirectory(builtin);
        Directory.CreateDirectory(user);
        var registry = new SkillRegistry(builtin, user);
        registry.Reload();

        var hub = new ToolHostHub(
            registry, null, new AppConfig(),
            new FileTools(_dir, sandboxRunner: null),
            shellReviewer: (_, _) => Task.FromResult((false, "测试拒绝")));

        var result = await hub.ExecuteToolAsync(
            FileTools.RunShellTool, """{"command":"rm -rf /"}""", new SkillsConfig());

        Assert.Contains("未通过安全审核", result);
        Assert.Contains("测试拒绝", result);
    }
}
