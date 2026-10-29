using LogAgent.Routing;
using LogAgent.Shared;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Core;

/// <summary>
/// run_shell 的 AI 安全审核（替代人工审批）。
/// 沙箱白名单已经挡掉明显危险命令，这一层再拦「组合用法 / 越权 / 与排障无关」的命令。
/// 审核失败一律拒绝（fail-closed）——宁可让模型换一种取证方式，也不放行未经审核的命令。
/// </summary>
public sealed class ShellCommandReviewer(ModelRouter router)
{
    public sealed record Verdict(bool Allow, string Reason);

    public async Task<Verdict> ReviewAsync(string command, string purpose, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command)) return new Verdict(false, "命令为空");

        var request = string.IsNullOrWhiteSpace(purpose)
            ? $"命令：{command}"
            : $"命令：{command}\n执行目的：{purpose}";
        try
        {
            var result = await router.CompleteAsync(
                TaskType.Chat,
                [ChatTurn.System(Prompts.ShellReview), ChatTurn.User(request)],
                maxTokens: 120, temperature: 0, ct: ct);

            var text = result.Content.Trim();
            var allow = text.StartsWith("ALLOW", StringComparison.OrdinalIgnoreCase);
            var reason = text.Length > 5 ? text[5..].Trim(' ', ':', '：', '-', '\n') : "";
            return new Verdict(allow, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Verdict(false, $"审核不可用（{ex.GetType().Name}）");
        }
    }
}
