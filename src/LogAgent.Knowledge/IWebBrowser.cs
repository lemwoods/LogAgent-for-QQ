namespace LogAgent.Knowledge;

/// <summary>
/// 网页渲染结果：标题 + 渲染后的可见正文 + 截图（PNG，可能为空）。
/// 与 <see cref="WebPage"/> 的区别是正文来自浏览器执行 JavaScript 后的 DOM，能覆盖 SPA/动态页面。
/// </summary>
public sealed record BrowserPage(string Url, string Title, string Text, byte[]? Screenshot, int Width, int Height);

/// <summary>
/// 无头浏览器抽象（web_screenshot 工具的底座）。
/// 抽成接口是为了让工具层单测不依赖本机是否安装 Firefox。
/// </summary>
public interface IWebBrowser
{
    /// <summary>打开 URL、等待页面渲染，返回可见正文与截图。</summary>
    Task<BrowserPage> RenderAsync(string url, int width, int height, bool fullPage, int maxTextChars,
        CancellationToken ct = default);
}
