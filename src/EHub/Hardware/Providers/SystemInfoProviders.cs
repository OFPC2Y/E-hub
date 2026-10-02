using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using EHub.Diagnostics;
using Microsoft.Win32;

namespace EHub.Hardware.Providers;

/// <summary>
/// CPU 型号。原版依次试 PowerShell 和 wmic（每次都要起进程），
/// 这里直接读注册表，启动阶段零开销。
/// </summary>
internal sealed class CpuModelProvider
{
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    private string? _cached;

    public string Get()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProcessorKey);
            if (key?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0)
            {
                return _cached = name.Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 CPU 型号失败: {ex.Message}");
        }

        return _cached = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU";
    }
}

/// <summary>物理内存占用。</summary>
internal sealed class MemoryProvider
{
    private const double BytesPerMegabyte = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public (double TotalMb, double UsedMb, double Percent) Read()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhys > 0)
            {
                var used = status.TotalPhys - status.AvailPhys;
                return (
                    Math.Floor(status.TotalPhys / BytesPerMegabyte),
                    Math.Floor(used / BytesPerMegabyte),
                    Math.Round(used / (double)status.TotalPhys * 100, 1));
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取内存失败", ex);
        }

        return (0, 0, 0);
    }
}

/// <summary>系统已运行时长，格式与原来一致。</summary>
internal sealed class UptimeProvider
{
    public string? Read()
    {
        try
        {
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var hours = (int)elapsed.TotalHours;
            var minutes = elapsed.Minutes;

            return hours >= 24
                ? $"{hours / 24}天{hours % 24}小时{minutes}分"
                : $"{hours}小时{minutes}分";
        }
        catch (Exception ex)
        {
            Log.Error("读取运行时间失败", ex);
            return null;
        }
    }
}

/// <summary>ICMP 延迟。原版用 ping3（要自己开原始套接字），这里用 BCL 的 Ping。</summary>
internal sealed class PingProvider
{
    public async Task<double?> ReadAsync(string target, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, timeoutMilliseconds)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (reply.Status == IPStatus.Success)
            {
                return Math.Round((double)reply.RoundtripTime, 1);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出时取消，不算错误
        }
        catch (Exception ex)
        {
            Log.Debug($"Ping {target} 失败: {ex.Message}");
        }

        return null;
    }
}
