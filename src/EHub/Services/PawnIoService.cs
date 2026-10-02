using System.Diagnostics;
using System.IO;
using EHub.Configuration;
using EHub.Diagnostics;
using EHub.Hardware;

namespace EHub.Services;

public readonly record struct PawnIoResult(bool Success, bool NeedsRestart, string Message);

/// <summary>
/// PawnIO 内核驱动的检测与安装。
/// Intel 12 代及以上的 CPU 温度、以及多数主板的风扇转速，都要这个驱动才读得到。
/// </summary>
public sealed class PawnIoService(TelemetryCollector collector)
{
    private const int SetupTimeoutMilliseconds = 600_000;

    public bool IsNeeded() => collector.Hardware.IsPawnIoNeeded(collector.Hardware.Scan());

    /// <summary>运行安装向导，并在结束后确认传感器是否真的可用了。</summary>
    public PawnIoResult RunSetup()
    {
        var executable = AppPaths.PawnIoSetup;
        if (!File.Exists(executable))
        {
            return new PawnIoResult(false, false, $"未找到安装程序: {executable}");
        }

        Process? process;
        try
        {
            // 交互式 GUI 向导：UseShellExecute 让它自己申请提权
            process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            return new PawnIoResult(false, false, $"启动 PawnIO_setup 失败: {ex.Message}");
        }

        if (process is null)
        {
            return new PawnIoResult(false, false, "启动 PawnIO_setup 失败：进程未能创建");
        }

        try
        {
            using (process)
            {
                if (!process.WaitForExit(SetupTimeoutMilliseconds))
                {
                    // 用户还在走向导，不能杀掉它
                    return new PawnIoResult(
                        false, true,
                        $"安装向导仍未结束（等待超过 {SetupTimeoutMilliseconds / 60_000} 分钟）。\n"
                        + "请完成安装后重新启动本程序。");
                }

                collector.Hardware.Reset();

                if (!collector.Hardware.IsAvailable)
                {
                    var detail = collector.Hardware.LastError;
                    var message = $"安装程序已退出（代码: {process.ExitCode}），但 LibreHardwareMonitor 未能加载";
                    if (detail.Length > 0)
                    {
                        message += $"\n{detail}";
                    }

                    return new PawnIoResult(false, false, message);
                }
            }
        }
        catch (Exception ex)
        {
            return new PawnIoResult(false, false, $"等待安装程序失败: {ex.Message}");
        }

        if (IsNeeded())
        {
            return new PawnIoResult(
                false, true,
                "驱动已安装，但传感器仍未就绪。\n请重新启动本程序后再查看。");
        }

        return new PawnIoResult(true, false, "PawnIO 驱动安装成功，CPU 传感器数据已可用");
    }
}
