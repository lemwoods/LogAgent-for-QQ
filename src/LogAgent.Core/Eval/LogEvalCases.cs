using System.Text;

namespace LogAgent.Core;

/// <summary>
/// 评估样例的单一来源（Tier 1 结构断言 / Tier 2 真模型评估 / 无头版 <c>eval</c> 子命令共用）。
/// · <c>Anchor</c>：必须活着进入模型材料的证据锚点（解析与降采样回归用）；
/// · <c>AnchorLine</c>：锚点所在行号；0 表示不校验具体行（如长日志被聚类折叠）；
/// · <c>AcceptAny</c>：报告里命中任一即可接受的根因表述（避免措辞脆弱）；
/// · <c>Decoy</c>：日志里根本不存在的东西——报告若提到它，说明模型在编（幻觉检测）。
/// </summary>
public sealed record LogEvalCase(
    string Name,
    string Log,
    string Anchor,
    int AnchorLine,
    string[] AcceptAny,
    string Decoy);

/// <summary>
/// 内置评估样例：按 Minecraft 常见故障谱裁剪，覆盖「显式 ERROR」到「无 ERROR 级别的原生崩溃」，
/// 以及长日志降采样。放在 Core 而不是测试工程，是为了让生产服务器能用 <c>LogAgent.Headless eval</c> 跑同一批样例。
/// </summary>
public static class LogEvalCases
{
    public static readonly LogEvalCase[] All =
    [
        new("缺失前置模组",
            """
            [10:00:01] [main/INFO]: Loading 87 mods
            [10:00:02] [main/ERROR]: java.lang.NoClassDefFoundError: net/minecraftforge/fml/loading/ModLoadingException
            [10:00:03] [main/INFO]: Stopping
            """,
            "NoClassDefFoundError", 2,
            ["缺少", "前置", "依赖", "NoClassDefFoundError"],
            "QuantumOreMod"),

        new("版本不匹配",
            """
            [10:01:01] [main/INFO]: Checking mod dependencies
            [10:01:02] [main/ERROR]: Mod create requires minecraft 1.20.1 but 1.19.2 is present
            [10:01:03] [main/INFO]: Aborting
            """,
            "requires minecraft", 2,
            ["版本", "1.20.1", "不匹配", "requires"],
            "AetheriumCore"),

        new("Java 版本过低",
            """
            [10:02:01] [main/INFO]: Launching JVM
            [10:02:02] [main/ERROR]: java.lang.UnsupportedClassVersionError: com/example/Mod has been compiled by a more recent version
            [10:02:03] [main/INFO]: Exiting
            """,
            "UnsupportedClassVersionError", 2,
            ["Java", "版本过低", "class file version", "UnsupportedClassVersionError"],
            "CrystalForgeMod"),

        new("内存不足",
            """
            [10:03:01] [main/INFO]: Starting integrated server
            [10:03:02] [main/ERROR]: java.lang.OutOfMemoryError: Java heap space
            [10:03:03] [main/INFO]: Saving chunks
            """,
            "OutOfMemoryError", 2,
            ["内存", "OutOfMemoryError", "heap", "堆"],
            "NebulaWrench"),

        new("注册表冲突",
            """
            [10:04:01] [main/INFO]: Registering blocks
            [10:04:02] [main/ERROR]: Duplicate registration for minecraft:stone found in mod a and mod b
            [10:04:03] [main/INFO]: Stopping
            """,
            "Duplicate registration", 2,
            ["重复", "冲突", "Duplicate registration", "注册"],
            "VoidHarvester"),

        new("磁盘写满",
            """
            [10:05:01] [main/INFO]: Saving level
            [10:05:02] [main/ERROR]: java.io.IOException: No space left on device
            [10:05:03] [main/INFO]: Retrying
            """,
            "No space left on device", 2,
            ["磁盘", "空间", "No space left"],
            "SolarLoom"),

        new("文件权限不足",
            """
            [10:06:01] [main/INFO]: Scanning mods directory
            [10:06:02] [main/ERROR]: java.nio.file.AccessDeniedException: /sdcard/mods/example.jar
            [10:06:03] [main/INFO]: Skipping
            """,
            "AccessDeniedException", 2,
            ["权限", "AccessDeniedException", "拒绝访问"],
            "FrostPiston"),

        // ---------- 长尾：错误不在第一屏、或只有堆栈/原生崩溃 ----------

        new("Mixin 注入失败",
            """
            [11:00:01] [main/INFO]: Mixin bootstrap
            [11:00:02] [main/INFO]: Loading 132 mods
            [11:00:03] [main/WARN]: Mixin config mixins.example.json is missing
            [11:00:04] [main/ERROR]: org.spongepowered.asm.mixin.injection.throwables.InvalidInjectionException: Critical injection failure in ExampleMixin
            [11:00:05] [main/INFO]: Stopping
            """,
            "InvalidInjectionException", 4,
            ["Mixin", "注入", "InvalidInjection"],
            "EmberCircuit"),

        new("加载器混装",
            """
            [11:01:01] [main/INFO]: Loading 120 mods
            [11:01:02] [main/ERROR]: java.lang.RuntimeException: fabric-api.jar is not a valid mod file for this loader
            [11:01:03] [main/INFO]: Stopping
            """,
            "not a valid mod file", 2,
            ["Fabric", "Forge", "加载器", "不兼容", "valid mod file"],
            "ThornAssembly"),

        new("光影不兼容",
            """
            [11:02:01] [main/INFO]: Loading shader pack
            [11:02:02] [main/INFO]: Renderer: OptiFine
            [11:02:03] [main/INFO]: Checking compatibility
            [11:02:04] [main/ERROR]: java.lang.IllegalStateException: Shader pack BSL is not compatible with this renderer version
            [11:02:05] [main/INFO]: Disabling shaders
            """,
            "not compatible with this renderer", 4,
            ["光影", "Shader", "OptiFine", "渲染器", "不兼容"],
            "GlacierBloom"),

        new("区块损坏",
            """
            [11:03:01] [main/INFO]: Loading world
            [11:03:02] [main/INFO]: Scanning region files
            [11:03:03] [main/ERROR]: java.io.IOException: Chunk file at 0,0 is missing or corrupt
            [11:03:04] [main/INFO]: Recovering
            """,
            "Chunk file at", 3,
            ["区块", "Chunk", "世界", "损坏"],
            "MarbleDrift"),

        new("启动器 Java 路径错误",
            """
            [11:04:01] [main/INFO]: Detecting java
            [11:04:02] [main/ERROR]: java.io.IOException: CreateProcess error=2, 系统找不到指定的文件
            [11:04:03] [main/INFO]: Launch aborted
            """,
            "CreateProcess error=2", 2,
            ["Java", "路径", "CreateProcess", "找不到"],
            "CopperLatch"),

        new("模组压缩包损坏",
            """
            [11:05:01] [main/INFO]: Reading mod files
            [11:05:02] [main/INFO]: Found example-mod.jar
            [11:05:03] [main/INFO]: Parsing archive
            [11:05:04] [main/ERROR]: java.util.zip.ZipException: zip END header not found
            [11:05:05] [main/INFO]: Skipping mod
            """,
            "zip END header not found", 4,
            ["zip", "压缩", "损坏", "END header"],
            "SilentAnvil"),

        new("重复模组版本",
            """
            [11:06:01] [main/INFO]: Scanning mods
            [11:06:02] [main/WARN]: Found two files for examplemod
            [11:06:03] [main/ERROR]: java.lang.IllegalStateException: Duplicate mod examplemod (1.2.0 and 1.3.0)
            [11:06:04] [main/INFO]: Stopping
            """,
            "Duplicate mod", 3,
            ["重复", "Duplicate", "版本", "两个"],
            "HollowReed"),

        new("服务端卡顿看门狗",
            """
            [11:07:01] [Server thread/INFO]: Preparing spawn area
            [11:07:02] [Server thread/WARN]: Can't keep up! Is the server overloaded?
            [11:07:03] [Server thread/INFO]: Saving chunks
            [11:07:04] [Server thread/INFO]: Flushing
            [11:07:05] [Server thread/INFO]: Waiting
            [11:07:06] [Server thread/INFO]: Tick 100
            [11:07:07] [Server thread/INFO]: Tick 200
            [11:07:08] [Server thread/ERROR]: A single server tick took 60.00 seconds (should be max 0.05)
            """,
            "single server tick took", 8,
            ["tick", "卡顿", "看门狗", "watchdog", "超时", "性能"],
            "RustGear"),

        new("实体 tick 崩溃",
            """
            [11:08:01] [Server thread/INFO]: Loading entities
            [11:08:02] [Server thread/ERROR]: Encountered an unexpected exception
            java.lang.NullPointerException: Cannot invoke "Entity.getType()" because "entity" is null
            	at net.minecraft.world.entity.Entity.tick(Entity.java:123)
            [11:08:03] [Server thread/INFO]: This crash report has been saved to: ./crash-reports/crash.txt
            """,
            "NullPointerException", 3,
            ["NullPointerException", "空指针", "实体", "entity"],
            "DustWeaver"),

        new("世界版本过新",
            """
            [11:09:01] [main/INFO]: Opening world
            [11:09:02] [main/INFO]: Reading level.dat
            [11:09:03] [main/ERROR]: java.lang.RuntimeException: World was created with a newer version (1.21) than this game (1.20.1)
            """,
            "newer version", 3,
            ["版本", "newer version", "世界", "降级"],
            "IvorySpire"),

        new("原生崩溃（无 ERROR 级别）",
            """
            # A fatal error has been detected by the Java Runtime Environment:
            #  SIGSEGV (0xb) at pc=0x0000007f8a2b1c40, pid=4242, tid=4243
            # JRE version: OpenJDK Runtime Environment (21.0.2)
            # Problematic frame:
            # C  [liblwjgl.so+0x1234]
            """,
            "SIGSEGV", 2,
            ["SIGSEGV", "崩溃", "原生", "native", "JVM"],
            "UmberFang"),

        new("正版认证失败",
            """
            [11:10:01] [main/INFO]: Connecting to server
            [11:10:02] [main/INFO]: Authenticating
            [11:10:03] [main/ERROR]: com.mojang.authlib.exceptions.AuthenticationException: Failed to verify username
            [11:10:04] [main/INFO]: Disconnected
            """,
            "Failed to verify username", 3,
            ["认证", "验证", "username", "登录", "正版"],
            "PaleHarbor"),

        // 长日志降采样：只有 1 个错误簇，但要确认它没被 60 行 INFO 淹没（行号 0 = 不校验具体行）
        new("长日志降采样",
            LongLog(),
            "OutOfMemoryError", 0,
            ["内存", "OutOfMemoryError", "heap", "堆"],
            "GlassMeridian"),
    ];

    /// <summary>60 行日志，错误只出现在第 42 行——用于验证降采样不会把唯一信号丢掉。</summary>
    private static string LongLog()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 60; i++)
        {
            sb.AppendLine(i == 42
                ? $"[10:11:{i % 60:00}] [main/ERROR]: java.lang.OutOfMemoryError: Java heap space"
                : $"[10:11:{i % 60:00}] [main/INFO]: Loading chunk {i}");
        }
        return sb.ToString();
    }
}
