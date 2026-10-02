using EHub.Diagnostics;
using Microsoft.Win32;

namespace EHub.Services;

/// <summary>
/// 开机自启开关。
///
/// 原 Python 版有「开机自启监控」这个设置项，也能存进 config.json，
/// 但没有任何代码读它 —— 勾了等于没勾。这里用 HKCU\Run 真正落地。
/// HKCU 不需要管理员权限，但本程序本来就是提权运行的，同一用户下读写正常。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EHubSystemMonitor";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                Log.Warn(@"无法打开 HKCU\Run，跳过开机自启设置");
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("已取消开机自启");
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Log.Warn("无法确定 exe 路径，跳过开机自启设置");
                return;
            }

            key.SetValue(ValueName, $"\"{executable}\"");
            Log.Info($"已设置开机自启: {executable}");
        }
        catch (Exception ex)
        {
            Log.Warn($"设置开机自启失败: {ex.Message}");
        }
    }
}
