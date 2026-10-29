using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

/// <summary>
/// 配置文件权限（docs/08 P2）：config.json 存明文 API Key，Unix 下必须只允许属主读写。
/// </summary>
[Collection("config-dir-env")]
public class ConfigPermissionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"la-cfgperm-{Guid.NewGuid():N}");
    private readonly string? _previous;

    public ConfigPermissionTests()
    {
        _previous = Environment.GetEnvironmentVariable("LOGAGENT_CONFIG_DIR");
        Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOGAGENT_CONFIG_DIR", _previous);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [UnixOnlyFact]
    public void Save_SetsOwnerOnlyPermissions()
    {
        AppConfigStore.Save(AppConfig.CreateDefault());

        var mode = File.GetUnixFileMode(AppConfigStore.GetConfigPath());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
