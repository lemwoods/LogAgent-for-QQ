using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using LogAgent.Shared.Platform;

namespace LogAgent.Knowledge;

/// <summary>
/// Headless Firefox 渲染器：启动独立 profile 的 Firefox（--headless --marionette），
/// 通过 Marionette 协议导航、等待 JS 渲染、读取可见正文并截图。
/// <para>为什么不用 HttpClient：动态页面/SPA 的正文由脚本生成，静态 HTML 里往往为空；
/// 浏览器渲染后才能拿到用户真正看到的内容。</para>
/// <para>SSRF：启动前校验顶层 URL；但页面内的子资源仍可能访问内网（浏览器行为），
/// 因此该工具只应暴露给可信的模型调用方。</para>
/// </summary>
public sealed class FirefoxBrowser : IWebBrowser, IDisposable
{
    /// <summary>截图 PNG 上限（超过则拒绝返回，避免把巨大文件塞进上下文/磁盘）。</summary>
    public const int MaxScreenshotBytes = 20 * 1024 * 1024;

    private static readonly string[] ExecutableNames =
        PlatformInfo.Current == OsFamily.Windows ? ["firefox.exe"] : ["firefox", "firefox-esr"];

    private readonly string? _configuredPath;
    private readonly bool _allowPrivate;
    private readonly int _timeoutSeconds;
    private readonly SemaphoreSlim _gate;
    private string? _resolved;

    /// <param name="firefoxPath">Firefox 路径；留空自动探测。</param>
    /// <param name="allowPrivate">是否允许本机/内网地址（仅测试用，生产保持 false）。</param>
    /// <param name="timeoutSeconds">单次渲染总超时（秒），范围 10-300。</param>
    /// <param name="maxConcurrent">并发渲染上限，范围 1-4。</param>
    public FirefoxBrowser(string? firefoxPath = null, bool allowPrivate = false,
        int timeoutSeconds = 90, int maxConcurrent = 1)
    {
        _configuredPath = string.IsNullOrWhiteSpace(firefoxPath) ? null : firefoxPath.Trim();
        _allowPrivate = allowPrivate;
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 10, 300);
        _gate = new SemaphoreSlim(Math.Clamp(maxConcurrent, 1, 4));
    }

    /// <summary>解析到的 Firefox 可执行文件；未安装时为 null（启动日志与工具提示用）。</summary>
    public string? ExecutablePath => _resolved ??= FindExecutable(_configuredPath);

    /// <summary>
    /// 探测 Firefox：显式路径 &gt; PATH &gt; 各平台常见安装位置。
    /// 返回 null 表示未安装；显式配置了但文件不存在时同样返回 null（由调用方提示修正配置）。
    /// </summary>
    public static string? FindExecutable(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = configuredPath.Trim();
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        foreach (var name in ExecutableNames)
        {
            var found = FindOnPath(name);
            if (found is not null) return found;
        }

        foreach (var candidate in KnownInstallPaths())
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    public async Task<BrowserPage> RenderAsync(string url, int width, int height, bool fullPage,
        int maxTextChars, CancellationToken ct = default)
    {
        if (!_allowPrivate) await WebFetchCore.EnsurePublicHttpUrlAsync(url, ct);

        var executable = ExecutablePath ?? throw new InvalidOperationException(
            "未找到 Firefox。请先安装（Termux：pkg install x11-repo firefox；Windows/macOS：官方 Firefox），" +
            "或在配置 Browser.FirefoxPath 中指定 firefox 可执行文件路径");

        width = Math.Clamp(width, 320, 1920);
        height = Math.Clamp(height, 240, 2400);
        maxTextChars = Math.Clamp(maxTextChars, 500, 12000);

        await _gate.WaitAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        var token = timeout.Token;

        var profileDir = Path.Combine(Path.GetTempPath(), "logagent-ff-" + Guid.NewGuid().ToString("N"));
        Process? process = null;
        var stderr = new StringBuilder();
        try
        {
            Directory.CreateDirectory(profileDir);
            var port = GetFreePort();
            await File.WriteAllTextAsync(Path.Combine(profileDir, "user.js"), BuildUserJs(port), token);

            (process, stderr) = StartFirefox(executable, profileDir, width, height);
            using var client = await ConnectAsync(port, process, stderr, token);
            await using var marionette = new MarionetteClient(client);
            await marionette.HandshakeAsync(token);

            await marionette.SendAsync("WebDriver:NewSession",
                new JsonObject { ["capabilities"] = new JsonObject() }, token);
            var window = await marionette.SendAsync("WebDriver:NewWindow",
                new JsonObject { ["type"] = "tab", ["focus"] = true }, token);
            var handle = ValueString(window?["handle"]);
            if (handle.Length == 0) throw new InvalidOperationException("Firefox 未返回可用的浏览窗口");
            await marionette.SendAsync("WebDriver:SwitchToWindow",
                new JsonObject { ["handle"] = handle }, token);
            await marionette.SendAsync("WebDriver:SetWindowRect",
                new JsonObject { ["width"] = width, ["height"] = height }, token);
            await marionette.SendAsync("WebDriver:SetTimeouts",
                new JsonObject { ["pageLoad"] = 30000, ["script"] = 15000 }, token);

            await marionette.SendAsync("WebDriver:Navigate",
                new JsonObject { ["url"] = url }, token);
            await WaitForReadyAsync(marionette, token);
            // 给 SPA 的异步渲染留一点时间（readyState=complete 之后仍可能有首屏请求）
            await Task.Delay(400, token);

            var title = ValueString(await marionette.SendAsync("WebDriver:GetTitle", null, token));
            var text = ValueString(await marionette.SendAsync("WebDriver:ExecuteScript",
                new JsonObject
                {
                    ["script"] = "const root = document.body || document.documentElement; " +
                                 "return root ? (root.innerText || root.textContent || '') : '';",
                    ["args"] = new JsonArray(),
                }, token));
            var truncated = text.Length > maxTextChars;
            if (truncated) text = text[..maxTextChars];

            var base64 = ValueString(await marionette.SendAsync("WebDriver:TakeScreenshot",
                new JsonObject { ["full"] = fullPage }, token));
            byte[]? screenshot = null;
            if (base64.Length > 0)
            {
                screenshot = Convert.FromBase64String(base64);
                if (screenshot.Length > MaxScreenshotBytes)
                    throw new InvalidOperationException("截图过大（超过 20MB），请降低窗口尺寸或关闭整页截图");
            }

            await TryQuitAsync(marionette);
            return new BrowserPage(url, title.Length == 0 ? url : title, text, screenshot, width, height);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"网页渲染超时（>{_timeoutSeconds} 秒）");
        }
        finally
        {
            KillProcess(process);
            TryDeleteDirectory(profileDir);
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static (Process Process, StringBuilder Stderr) StartFirefox(
        string executable, string profileDir, int width, int height)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--no-remote");
        psi.ArgumentList.Add("--marionette");
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(profileDir);
        psi.ArgumentList.Add("--window-size");
        psi.ArgumentList.Add($"{width},{height}");

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Firefox 进程启动失败");
        var stderr = new StringBuilder();
        // 必须持续排空管道，否则输出较多时 Firefox 会阻塞；同时保留尾部用于报错。
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (stderr)
            {
                if (stderr.Length < 8192) stderr.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, stderr);
    }

    private static async Task<TcpClient> ConnectAsync(int port, Process process, StringBuilder stderr,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Firefox 启动后立即退出（退出码 {process.ExitCode}）{StderrTail(stderr)}");

            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
                return client;
            }
            catch (SocketException ex)
            {
                last = ex;
                client.Dispose();
                await Task.Delay(200, ct);
            }
        }
        throw new InvalidOperationException($"Firefox 未在 20 秒内就绪{StderrTail(stderr)}", last);
    }

    private static async Task WaitForReadyAsync(MarionetteClient marionette, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var state = ValueString(await marionette.SendAsync("WebDriver:ExecuteScript",
                new JsonObject
                {
                    ["script"] = "return document.readyState;",
                    ["args"] = new JsonArray(),
                }, ct));
            if (state is "complete" or "interactive") return;
            await Task.Delay(150, ct);
        }
    }

    private static async Task TryQuitAsync(MarionetteClient marionette)
    {
        try
        {
            using var quitCts = new CancellationTokenSource(3000);
            await marionette.SendAsync("Marionette:Quit",
                new JsonObject { ["flags"] = new JsonArray("eForceQuit") }, quitCts.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            // 退出失败不影响结果，finally 会强杀进程树。
        }
    }

    private static void KillProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // 进程已退出/无权限：尽力而为。
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string StderrTail(StringBuilder stderr)
    {
        lock (stderr)
        {
            var text = stderr.ToString().Trim();
            if (text.Length == 0) return "";
            if (text.Length > 600) text = text[^600..];
            return "\nFirefox 输出：\n" + text;
        }
    }

    private static string ValueString(JsonNode? node)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue("value", out var value)) node = value;
        if (node is null) return "";
        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Null) return "";
        try
        {
            return node.GetValue<string>() ?? "";
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string BuildUserJs(int port) => $"""
        user_pref("marionette.port", {port});
        user_pref("browser.shell.checkDefaultBrowser", false);
        user_pref("browser.startup.page", 0);
        user_pref("browser.startup.homepage", "about:blank");
        user_pref("browser.startup.homepage_override.mstone", "ignore");
        user_pref("startup.homepage_welcome_url", "");
        user_pref("startup.homepage_welcome_url.additional", "");
        user_pref("datareporting.policy.dataSubmissionEnabled", false);
        user_pref("toolkit.telemetry.enabled", false);
        user_pref("browser.sessionstore.resume_from_crash", false);
        user_pref("app.update.enabled", false);
        """;

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                           or PathTooLongException)
            {
                // PATH 里可能有非法路径，跳过继续找。
            }
        }
        return null;
    }

    private static IEnumerable<string> KnownInstallPaths()
    {
        if (PlatformInfo.Current == OsFamily.Windows)
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (!string.IsNullOrEmpty(root))
                    yield return Path.Combine(root, "Mozilla Firefox", "firefox.exe");
            }
        }
        else if (PlatformInfo.Current == OsFamily.MacOS)
        {
            yield return "/Applications/Firefox.app/Contents/MacOS/firefox";
        }
        else
        {
            yield return "/usr/bin/firefox";
            yield return "/usr/lib/firefox/firefox";
            yield return "/opt/firefox/firefox";
        }
    }

    /// <summary>
    /// 最小 Marionette 客户端（Firefox 测试协议，v3）：长度前缀 + JSON 数组。
    /// 只实现本项目需要的命令，不引入 WebDriver 依赖。
    /// </summary>
    private sealed class MarionetteClient(TcpClient client) : IAsyncDisposable
    {
        private readonly NetworkStream _stream = client.GetStream();
        private int _id;

        public async Task HandshakeAsync(CancellationToken ct)
        {
            var hello = await ReadMessageAsync(ct);
            var protocol = hello?["marionetteProtocol"]?.GetValue<int>() ?? 0;
            if (protocol < 3)
                throw new InvalidOperationException($"Firefox Marionette 协议版本不受支持（{protocol}）");
        }

        public async Task<JsonNode?> SendAsync(string command, JsonObject? parameters, CancellationToken ct)
        {
            var id = ++_id;
            var payload = new JsonArray { 0, id, command, parameters ?? new JsonObject() }.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(payload);
            var prefix = Encoding.ASCII.GetBytes($"{bytes.Length}:");
            await _stream.WriteAsync(prefix, ct);
            await _stream.WriteAsync(bytes, ct);

            while (true)
            {
                var message = await ReadMessageAsync(ct);
                if (message is not JsonArray array || array.Count < 4) continue;
                if ((array[0]?.GetValue<int>() ?? -1) != 1) continue;
                if ((array[1]?.GetValue<int>() ?? -1) != id) continue;
                if (array[2] is JsonObject error)
                {
                    var detail = error["message"]?.GetValue<string>() ?? error.ToJsonString();
                    throw new InvalidOperationException($"Firefox {command} 失败：{detail}");
                }
                return array[3];
            }
        }

        private async Task<JsonNode?> ReadMessageAsync(CancellationToken ct)
        {
            var lengthText = new StringBuilder(10);
            var one = new byte[1];
            while (true)
            {
                var read = await _stream.ReadAsync(one, ct);
                if (read == 0) throw new IOException("Firefox Marionette 连接已关闭");
                if (one[0] == (byte)':') break;
                if (lengthText.Length >= 10) throw new InvalidOperationException("Marionette 消息长度异常");
                lengthText.Append((char)one[0]);
            }

            if (!int.TryParse(lengthText.ToString(), out var length) || length <= 0 || length > 64 * 1024 * 1024)
                throw new InvalidOperationException("Marionette 消息长度非法");

            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct);
                if (read == 0) throw new IOException("Firefox Marionette 连接已关闭");
                offset += read;
            }
            return JsonNode.Parse(buffer);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _stream.DisposeAsync();
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
