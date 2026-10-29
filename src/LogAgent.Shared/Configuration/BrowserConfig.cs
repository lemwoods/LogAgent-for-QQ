namespace LogAgent.Shared.Configuration;

/// <summary>
/// Headless 浏览器截图配置（web_screenshot 工具）。
/// Firefox 由 Termux 官方源或官方安装包提供；路径留空时按 PATH 与常见安装位置自动探测。
/// </summary>
public sealed class BrowserConfig
{
    /// <summary>
    /// 是否向模型暴露 web_screenshot 工具。默认开启；Firefox 缺失时工具返回可操作的安装提示，
    /// 不会阻断其余网页抓取能力（web_fetch 仍可用）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Firefox 可执行文件路径；留空自动探测 firefox / firefox-esr 及常见安装位置。</summary>
    public string FirefoxPath { get; set; } = "";

    /// <summary>单次渲染超时（秒），覆盖启动、导航、等待渲染与截图全过程。</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>并发渲染上限（每次渲染一个 Firefox 进程，手机上建议保持 1）。</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>截图保存目录；留空使用数据目录下的 files/screenshots。</summary>
    public string ScreenshotDirectory { get; set; } = "";
}
