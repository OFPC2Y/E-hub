using System.Diagnostics;
using System.IO;
using System.Text;
using EHub.Configuration;

namespace EHub.Diagnostics;

/// <summary>
/// 开发层日志：英文＋时间戳，滚动写入 exe 同目录的 logs/ehub.log。
/// 用户可见的错误由界面负责展示，这里只做排查用的记录。
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private const int BackupCount = 3;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string FilePath => AppPaths.LogFile;

    public static void Debug(string message) => Write("DEBUG", message);

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>「查看日志」菜单用：用系统默认程序打开日志文件。</summary>
    public static bool OpenInShell()
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug($"打开日志失败: {ex.Message}");
            return false;
        }
    }

    private static void Write(string level, string message, Exception? exception = null)
    {
        var thread = Thread.CurrentThread.Name ?? "main";
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{thread}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                RollIfNeeded();
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 日志写不了不能影响主程序
            }
        }
    }

    private static void RollIfNeeded()
    {
        var file = new FileInfo(AppPaths.LogFile);
        if (!file.Exists || file.Length < MaxBytes)
        {
            return;
        }

        for (var i = BackupCount - 1; i >= 1; i--)
        {
            var source = $"{AppPaths.LogFile}.{i}";
            if (File.Exists(source))
            {
                File.Move(source, $"{AppPaths.LogFile}.{i + 1}", overwrite: true);
            }
        }

        File.Move(AppPaths.LogFile, $"{AppPaths.LogFile}.1", overwrite: true);
    }
}
