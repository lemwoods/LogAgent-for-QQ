using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogAgent.Knowledge;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;

namespace LogAgent.Skills;

/// <summary>
/// 内置网页浏览工具：
/// · web_fetch —— 按 URL 抓取静态 HTML 并提取标题与正文（快、省资源）；
/// · web_screenshot —— 用 headless Firefox 执行 JavaScript 后读取可见正文并截图（动态页面/SPA 用）。
/// SSRF 守卫默认启用（拒绝内网/环回地址）；富化环节已自动抓取消息里的链接，本工具供模型主动查资料/补救。
/// </summary>
public sealed class WebTools(bool allowPrivate = false, IWebBrowser? browser = null, string? screenshotDir = null)
{
    public const string WebFetchTool = "web_fetch";
    public const string WebScreenshotTool = "web_screenshot";

    public const string WebFetchParameters =
        """{"type":"object","properties":{"url":{"type":"string","description":"要抓取的网页地址（http/https）"},"max_chars":{"type":"integer","description":"返回正文的最大字符数，默认 8000，上限 16000"}},"required":["url"]}""";

    public const string WebScreenshotParameters =
        """{"type":"object","properties":{"url":{"type":"string","description":"要打开并截图的网页地址（http/https，公网）"},"width":{"type":"integer","description":"视口宽度，默认 1280，范围 320-1920"},"height":{"type":"integer","description":"视口高度，默认 800，范围 240-2400"},"full_page":{"type":"boolean","description":"是否截取整页（默认 false，只截当前视口）"},"max_chars":{"type":"integer","description":"返回渲染后可见文本的最大字符数，默认 4000，上限 12000"}},"required":["url"]}""";

    private readonly string _screenshotDir = screenshotDir ?? AppConfigStore.GetScreenshotsDirectory();

    public event EventHandler<string>? ToolLog;

    public static bool IsBuiltinTool(string name) =>
        name is WebFetchTool or WebScreenshotTool;

    public IReadOnlyList<ChatToolDefinition> BuildToolDefinitions()
    {
        var tools = new List<ChatToolDefinition>
        {
            new(WebFetchTool,
                "抓取网页内容并提取标题与正文文本（自动去除脚本/导航等噪音）。用户想了解某个网页/文档/帖子内容，或需要验证链接、补充资料时使用。仅支持 http/https 公网地址；不接受图片/视频等非文本网页。",
                WebFetchParameters),
        };

        if (browser is not null)
        {
            tools.Add(new ChatToolDefinition(WebScreenshotTool,
                "用 headless Firefox 打开网页并执行 JavaScript，返回渲染后的可见正文，同时把界面截图保存到本机。适合动态渲染页面（SPA、需要脚本才显示内容）或 web_fetch 抓不到正文时使用；需要确认页面实际显示效果时也用它。仅支持 http/https 公网地址。若返回「未找到 Firefox」，说明本机没有安装，改用 web_fetch 或直接告知用户当前无法截图。",
                WebScreenshotParameters));
        }

        return tools;
    }

    public Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default) =>
        toolName switch
        {
            WebFetchTool => ExecuteFetchAsync(argumentsJson, ct),
            WebScreenshotTool => ExecuteScreenshotAsync(argumentsJson, ct),
            _ => Task.FromResult($"错误：未知工具 {toolName}"),
        };

    private async Task<string> ExecuteFetchAsync(string argumentsJson, CancellationToken ct)
    {
        string url;
        var maxChars = 8000;
        try
        {
            var node = ParseObject(argumentsJson);
            url = StringValue(node, "url").Trim();
            maxChars = IntValue(node, "max_chars", 8000);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return "错误：web_fetch 参数不是合法 JSON";
        }

        if (url.Length == 0) return "错误：缺少必填参数 url";
        maxChars = Math.Clamp(maxChars, 500, 16000);

        try
        {
            var page = await WebFetchCore.FetchAsync(url, allowPrivate, maxChars, ct);
            ToolLog?.Invoke(this, $"web_fetch {url} → {page.Text.Length} 字符（{page.Bytes} 字节）");
            var head = $"【网页：{page.Title}】\n来源：{page.Url}\n";
            if (page.Truncated)
                head += $"（正文较长，仅显示前 {maxChars} 字符——可用 read 类方式分段获取的替代：调大 max_chars 或抓取页面内更具体的子页面）\n";
            return head + "--- 正文 ---\n" + page.Text;
        }
        catch (Exception ex)
        {
            ToolLog?.Invoke(this, $"web_fetch {url} 失败：{ex.Message}");
            return $"错误：网页抓取失败（{ex.Message}）";
        }
    }

    private async Task<string> ExecuteScreenshotAsync(string argumentsJson, CancellationToken ct)
    {
        if (browser is null)
            return "错误：浏览器截图未启用（请检查配置 Browser.Enabled 与 Firefox 安装）";

        string url;
        int width, height, maxChars;
        bool fullPage;
        try
        {
            var node = ParseObject(argumentsJson);
            url = StringValue(node, "url").Trim();
            width = Math.Clamp(IntValue(node, "width", 1280), 320, 1920);
            height = Math.Clamp(IntValue(node, "height", 800), 240, 2400);
            fullPage = BoolValue(node, "full_page");
            maxChars = Math.Clamp(IntValue(node, "max_chars", 4000), 500, 12000);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return "错误：web_screenshot 参数不是合法 JSON";
        }

        if (url.Length == 0) return "错误：缺少必填参数 url";

        try
        {
            var page = await browser.RenderAsync(url, width, height, fullPage, maxChars, ct);
            string? path = null;
            if (page.Screenshot is { Length: > 0 } png)
            {
                Directory.CreateDirectory(_screenshotDir);
                path = Path.Combine(_screenshotDir,
                    $"web-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(path, png, ct);
            }

            ToolLog?.Invoke(this,
                $"web_screenshot {url} → {page.Text.Length} 字符，截图 {(path is null ? "无" : path)}");

            var sb = new StringBuilder();
            sb.AppendLine($"【网页（浏览器渲染）：{page.Title}】");
            sb.AppendLine($"来源：{page.Url}");
            if (path is not null && page.Screenshot is { } shot)
                sb.AppendLine($"截图：{path}（{shot.Length / 1024.0:F1}KB，{page.Width}x{page.Height}{(fullPage ? "，整页" : "")}）");
            else
                sb.AppendLine("截图：未生成");
            sb.AppendLine("--- 可见正文 ---");
            sb.AppendLine(page.Text.Length > 0 ? page.Text : "（页面没有可见文本）");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            ToolLog?.Invoke(this, $"web_screenshot {url} 失败：{ex.Message}");
            return $"错误：网页截图失败（{ex.Message}）";
        }
    }

    private static JsonObject? ParseObject(string argumentsJson) =>
        JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject;

    private static string StringValue(JsonObject? node, string key)
    {
        var value = node?[key];
        if (value is null) return "";
        return value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";
    }

    private static int IntValue(JsonObject? node, string key, int fallback)
    {
        var value = node?[key];
        if (value is null) return fallback;
        try
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.Number => value.GetValue<int>(),
                JsonValueKind.String => int.TryParse(value.GetValue<string>(), out var parsed) ? parsed : fallback,
                _ => fallback,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    private static bool BoolValue(JsonObject? node, string key)
    {
        var value = node?[key];
        if (value is null) return false;
        try
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetValue<string>(), out var parsed) && parsed,
                _ => false,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
