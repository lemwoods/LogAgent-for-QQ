using System.Text.Json.Nodes;
using LogAgent.Mcp;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Skills;

/// <summary>
/// 统一工具宿主：把四类能力聚合为模型可见的工具集合并统一路由执行——
/// ① 技能 HTTP 工具（skill.json）；② 指令技能读取（read_skill，SKILL.md 渐进式披露）；
/// ③ MCP 服务器工具（标准协议，名为 mcp__{server}__{tool}）；
/// ④ 内置文件工具（read_file / search_file / run_shell——大日志完整落盘后的选择性读取与检索）；
/// ⑤ 知识库检索（search_knowledge——让模型自己换关键词查历史案例，而不是只靠自动注入）。
/// </summary>
public sealed class ToolHostHub(
    SkillRegistry registry,
    McpManager? mcp,
    AppConfig appConfig,
    FileTools? fileTools = null,
    WebTools? webTools = null,
    Func<string, int, CancellationToken, Task<string>>? knowledgeSearch = null,
    Func<string, CancellationToken, Task<(bool Allow, string Reason)>>? shellReviewer = null) : ISkillToolHost
{
    /// <summary>读取技能说明的内置工具名。</summary>
    public const string ReadSkillTool = "read_skill";

    public const string ReadSkillToolName = ReadSkillTool;

    /// <summary>知识库检索工具名。</summary>
    public const string SearchKnowledgeTool = "search_knowledge";

    private const string ReadSkillParameters =
        """{"type":"object","properties":{"skill_id":{"type":"string","description":"技能 id（见系统提示中的可用技能列表）"},"file":{"type":"string","description":"可选：技能目录内的附属文件名（默认读取技能说明全文）"}},"required":["skill_id"]}""";

    private const string SearchKnowledgeParameters =
        """{"type":"object","properties":{"query":{"type":"string","description":"检索词：优先用报错原文、异常类名、模组名或关键症状"},"top_k":{"type":"integer","description":"返回条数，默认 3，最大 5"}},"required":["query"]}""";

    public IReadOnlyList<ChatToolDefinition> BuildToolDefinitions(SkillsConfig config)
    {
        var tools = new List<ChatToolDefinition>();
        tools.AddRange(registry.BuildToolDefinitions(config));

        if (registry.PromptSkills(config).Count > 0)
        {
            tools.Add(new ChatToolDefinition(ReadSkillTool,
                "读取技能的完整说明文档（SKILL.md）。当用户请求与某个技能描述匹配时，先读取再执行。",
                ReadSkillParameters));
        }

        if (mcp is not null && appConfig.Mcp.Enabled)
            tools.AddRange(mcp.GetToolDefinitions());

        if (fileTools is not null)
            tools.AddRange(fileTools.BuildToolDefinitions(shellEnabled: appConfig.Sandbox.Enabled));

        if (webTools is not null)
            tools.AddRange(webTools.BuildToolDefinitions());

        if (knowledgeSearch is not null)
        {
            tools.Add(new ChatToolDefinition(SearchKnowledgeTool,
                "检索本地知识库里的历史案例与排查经验。自动注入的参考案例不匹配、或需要更多证据时，自己换关键词再查；返回内容只是参考，须与当前日志核对。",
                SearchKnowledgeParameters));
        }

        return tools;
    }

    public string BuildPromptSkillsBlock(SkillsConfig config) => registry.BuildPromptSkillsBlock(config);

    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, SkillsConfig config, CancellationToken ct = default)
    {
        try
        {
            return await ExecuteCoreAsync(toolName, argumentsJson, config, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方取消：交给上层处理
        }
        catch (Exception ex)
        {
            // 工具失败是一次“观察”，不是整轮推理的终点：把错误交给模型，让它换路或如实说明
            return $"错误：工具 {toolName} 执行失败（{ex.GetType().Name}: {ex.Message}）";
        }
    }

    private async Task<string> ExecuteCoreAsync(string toolName, string argumentsJson, SkillsConfig config, CancellationToken ct)
    {
        if (string.Equals(toolName, ReadSkillTool, StringComparison.Ordinal))
            return ExecuteReadSkill(argumentsJson, config);

        if (toolName.StartsWith(McpProtocol.ToolPrefix, StringComparison.Ordinal))
        {
            if (mcp is null || !appConfig.Mcp.Enabled)
                return "错误：MCP 功能未启用";
            return await mcp.ExecuteToolAsync(toolName, argumentsJson, ct);
        }

        if (fileTools is not null && FileTools.IsBuiltinTool(toolName))
        {
            if (shellReviewer is not null &&
                string.Equals(toolName, FileTools.RunShellTool, StringComparison.Ordinal))
            {
                var command = ReadStringArg(argumentsJson, "command");
                if (string.IsNullOrWhiteSpace(command)) return "错误：缺少必填参数 command";

                var (allow, reason) = await shellReviewer(command, ct);
                if (!allow)
                    return string.IsNullOrWhiteSpace(reason)
                        ? "错误：命令未通过安全审核"
                        : $"错误：命令未通过安全审核（{reason}）";
            }
            return await fileTools.ExecuteAsync(toolName, argumentsJson, ct);
        }

        if (webTools is not null && WebTools.IsBuiltinTool(toolName))
            return await webTools.ExecuteAsync(toolName, argumentsJson, ct);

        if (string.Equals(toolName, SearchKnowledgeTool, StringComparison.Ordinal))
            return await ExecuteSearchKnowledgeAsync(argumentsJson, ct);

        return await registry.ExecuteToolAsync(toolName, argumentsJson, config, ct);
    }

    private static string ReadStringArg(string argumentsJson, string name)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject;
            return node?[name]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return "";
        }
    }

    private async Task<string> ExecuteSearchKnowledgeAsync(string argumentsJson, CancellationToken ct)
    {
        if (knowledgeSearch is null) return "错误：知识库检索未启用";

        string query;
        var topK = 3;
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject;
            query = node?["query"]?.GetValue<string>() ?? "";
            if (node?["top_k"] is { } topNode && topNode.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                topK = Math.Clamp(topNode.GetValue<int>(), 1, 5);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return "错误：search_knowledge 参数不是合法 JSON（应为 {\"query\":\"…\"}）";
        }

        if (string.IsNullOrWhiteSpace(query)) return "错误：缺少必填参数 query";
        return await knowledgeSearch(query.Trim(), topK, ct);
    }

    private string ExecuteReadSkill(string argumentsJson, SkillsConfig config)
    {
        string skillId;
        string? file = null;
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject;
            skillId = node?["skill_id"]?.GetValue<string>() ?? "";
            var fileNode = node?["file"];
            if (fileNode is not null && fileNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                var value = fileNode.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value)) file = value;
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return "错误：read_skill 参数不是合法 JSON（应为 {\"skill_id\":\"…\"}）";
        }
        if (string.IsNullOrWhiteSpace(skillId))
            return "错误：缺少必填参数 skill_id";
        return registry.ReadSkillContent(skillId, file, config);
    }
}
