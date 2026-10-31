using LogAgent.Knowledge;
using LogAgent.Skills;
using Xunit;

namespace LogAgent.Tests;

// ─────────────────────────── web_screenshot（headless Firefox） ───────────────────────────

/// <summary>仅在本机装了 Firefox 且能创建 user namespace 时运行；CI 容器里通常两者都不满足。</summary>
public sealed class FirefoxFactAttribute : FactAttribute
{
    public FirefoxFactAttribute()
    {
        if (FirefoxBrowser.FindExecutable() is null) Skip = "未安装 Firefox";
        // GitHub Actions 等容器禁止 unshare(CLONE_NEWPID)，Firefox 内容沙箱起不来，
        // marionette 永远不 ready；这不是代码回归，跳过而不是让 CI 红。
        else if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 })
            Skip = "CI 容器不允许 Firefox 创建 user namespace（unshare EPERM）";
    }
}

public class WebBrowserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"la-shot-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] ExpectedPng = [.. PngSignature, 1, 2, 3, 4];

    private sealed class FakeBrowser : IWebBrowser
    {
        public int Calls;
        public string? Url;
        public int Width;
        public int Height;
        public bool FullPage;
        public int MaxChars;

        public Task<BrowserPage> RenderAsync(string url, int width, int height, bool fullPage,
            int maxTextChars, CancellationToken ct = default)
        {
            Calls++;
            Url = url;
            Width = width;
            Height = height;
            FullPage = fullPage;
            MaxChars = maxTextChars;
            return Task.FromResult(new BrowserPage(url, "渲染页", "渲染后的正文 ABC",
                ExpectedPng, width, height));
        }
    }

    [Fact]
    public async Task Screenshot_SavesPngAndPassesClampedOptions()
    {
        var fake = new FakeBrowser();
        var tools = new WebTools(browser: fake, screenshotDir: _dir);

        var output = await tools.ExecuteAsync("web_screenshot",
            """{"url":"https://example.com/a","width":100,"height":5000,"full_page":true,"max_chars":100}""");

        Assert.Equal(1, fake.Calls);
        Assert.Equal("https://example.com/a", fake.Url);
        Assert.Equal(320, fake.Width);   // 下限
        Assert.Equal(2400, fake.Height); // 上限
        Assert.True(fake.FullPage);
        Assert.Equal(500, fake.MaxChars); // 下限

        Assert.Contains("【网页（浏览器渲染）：渲染页】", output);
        Assert.Contains("渲染后的正文 ABC", output);
        Assert.Contains("截图：", output);

        var file = Directory.GetFiles(_dir, "*.png").Single();
        Assert.Equal(ExpectedPng, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task Screenshot_NotExposedWhenBrowserDisabled()
    {
        var tools = new WebTools();
        Assert.DoesNotContain(tools.BuildToolDefinitions(), t => t.Name == WebTools.WebScreenshotTool);

        var output = await tools.ExecuteAsync("web_screenshot", """{"url":"https://example.com"}""");
        Assert.Contains("未启用", output);
    }

    [Fact]
    public async Task Screenshot_RejectsPrivateAddressBeforeStartingFirefox()
    {
        var tools = new WebTools(browser: new FirefoxBrowser(timeoutSeconds: 10), screenshotDir: _dir);
        var output = await tools.ExecuteAsync("web_screenshot", """{"url":"http://127.0.0.1:1/"}""");
        Assert.Contains("拒绝访问", output);
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : Array.Empty<string>());
    }

    [Fact]
    public async Task Screenshot_RejectsBrokenJson()
    {
        var tools = new WebTools(browser: new FakeBrowser(), screenshotDir: _dir);
        var output = await tools.ExecuteAsync("web_screenshot", "{oops");
        Assert.Contains("不是合法 JSON", output);
    }

    [Fact]
    public void FindExecutable_MissingConfiguredPathReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-firefox-{Guid.NewGuid():N}");
        Assert.Null(FirefoxBrowser.FindExecutable(missing));
    }

    [FirefoxFact]
    public async Task Firefox_RendersJavaScriptAndScreenshot()
    {
        using var server = new MiniHttpServer((_, _, _) => ("200 OK", "text/html",
            "<html><head><title>动态页</title></head><body><div id=\"x\">静态内容</div>" +
            "<script>document.getElementById('x').textContent='JS 渲染完成';</script></body></html>"));
        using var browser = new FirefoxBrowser(allowPrivate: true, timeoutSeconds: 90);

        var page = await browser.RenderAsync($"http://127.0.0.1:{server.Port}/", 800, 600, false, 2000);

        Assert.Contains("JS 渲染完成", page.Text);
        Assert.NotNull(page.Screenshot);
        Assert.True(page.Screenshot!.Length > 100);
        Assert.Equal(PngSignature, page.Screenshot[..PngSignature.Length]);
    }
}
