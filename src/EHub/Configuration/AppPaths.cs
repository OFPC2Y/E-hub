using System.IO;

namespace EHub.Configuration;

/// <summary>
/// 配置、日志、随包资源一律相对 exe 所在目录定位，不受工作目录影响。
/// </summary>
public static class AppPaths
{
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    public static string ConfigFile { get; } = Path.Combine(BaseDirectory, "config.json");

    public static string LogDirectory { get; } = Path.Combine(BaseDirectory, "logs");

    public static string LogFile { get; } = Path.Combine(LogDirectory, "ehub.log");

    public static string PawnIoSetup { get; } =
        Path.Combine(BaseDirectory, "external", "PawnIO_setup.exe");
}
