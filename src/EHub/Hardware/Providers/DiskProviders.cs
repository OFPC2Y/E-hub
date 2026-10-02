using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EHub.Diagnostics;

namespace EHub.Hardware.Providers;

/// <summary>
/// 各分区容量。原版先问 psutil.disk_partitions、失败再逐盘符 A:–Z: 硬探，
/// 而硬探在断开的网络盘上会长时间阻塞。这里直接用 GetLogicalDrives 拿盘符位图，
/// 再按 GetDriveType 过滤掉网络盘/光驱，从源头上避开阻塞。
/// </summary>
internal sealed class DiskUsageProvider
{
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;
    private const double BytesPerGigabyte = 1024 * 1024 * 1024;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveType(string rootPathName);

    public List<DiskUsage> Read()
    {
        var disks = new List<DiskUsage>();

        uint mask;
        try
        {
            mask = GetLogicalDrives();
        }
        catch (Exception ex)
        {
            Log.Error("枚举盘符失败", ex);
            return disks;
        }

        for (var index = 0; index < 26; index++)
        {
            if ((mask & (1u << index)) == 0)
            {
                continue;
            }

            var letter = (char)('A' + index);
            var root = $"{letter}:\\";

            uint type;
            try
            {
                type = GetDriveType(root);
            }
            catch (Exception ex)
            {
                Log.Debug($"GetDriveType({root}) 失败: {ex.Message}");
                continue;
            }

            // 只认本地固定盘和可移动盘；网络盘在断开时会让容量查询长时间挂住
            if (type != DriveFixed && type != DriveRemovable)
            {
                continue;
            }

            try
            {
                var info = new DriveInfo(root);
                if (!info.IsReady)
                {
                    continue;
                }

                var totalGb = info.TotalSize / BytesPerGigabyte;
                if (totalGb <= 0)
                {
                    continue;
                }

                var usedGb = (info.TotalSize - info.TotalFreeSpace) / BytesPerGigabyte;
                disks.Add(new DiskUsage
                {
                    Mount = $"{letter}:",
                    Total = Math.Round(totalGb, 1),
                    Used = Math.Round(usedGb, 1),
                    Percent = Math.Round(usedGb / totalGb * 100, 1),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"读取磁盘 {root} 失败: {ex.Message}");
            }
        }

        return disks;
    }
}

/// <summary>
/// 每块盘的读写速率。
///
/// 原版用 psutil 的累计字节数自己算差分，还得靠 PowerShell Get-Partition 把
/// "PhysicalDrive0" 映射成盘符。性能计数器的 PhysicalDisk 实例名本身就长成
/// "0 C:"，盘符直接读得出来，整段映射逻辑都不需要了。
/// </summary>
internal sealed class DiskIoProvider : IDisposable
{
    private const string CategoryName = "PhysicalDisk";
    private const string ReadCounter = "Disk Read Bytes/sec";
    private const string WriteCounter = "Disk Write Bytes/sec";

    /// <summary>实例名形如 "0 C:" 或 "1 C: D:"，一块盘挂多个分区时都要报到。</summary>
    private static readonly Regex LetterPattern = new(@"([A-Z]):", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Dictionary<string, Counters> _counters = new(StringComparer.Ordinal);
    private bool _categoryMissing;

    public Dictionary<string, object?> Read()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        lock (_gate)
        {
            if (_categoryMissing)
            {
                return result;
            }

            string[] instances;
            try
            {
                if (!PerformanceCounterCategory.Exists(CategoryName))
                {
                    _categoryMissing = true;
                    Log.Warn($"性能计数器类别 {CategoryName} 不存在，磁盘速率将不可用");
                    return result;
                }

                instances = new PerformanceCounterCategory(CategoryName).GetInstanceNames();
            }
            catch (Exception ex)
            {
                _categoryMissing = true;
                Log.Warn($"枚举 {CategoryName} 实例失败，磁盘速率将不可用", ex);
                return result;
            }

            foreach (var instance in instances)
            {
                if (instance == "_Total")
                {
                    continue;
                }

                var letters = ExtractLetters(instance);
                if (letters.Count == 0)
                {
                    continue;
                }

                var counters = GetOrCreate(instance);
                if (counters is null)
                {
                    continue;
                }

                double readKbs, writeKbs;
                try
                {
                    readKbs = counters.Read.NextValue() / 1024;
                    writeKbs = counters.Write.NextValue() / 1024;
                }
                catch (Exception ex)
                {
                    Log.Debug($"读取 {instance} 速率失败: {ex.Message}");
                    counters.Dispose();
                    _counters.Remove(instance);
                    continue;
                }

                foreach (var letter in letters)
                {
                    result[$"disk_io_{letter}_read"] = Math.Round(Math.Max(0, readKbs), 1);
                    result[$"disk_io_{letter}_write"] = Math.Round(Math.Max(0, writeKbs), 1);
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var counters in _counters.Values)
            {
                counters.Dispose();
            }

            _counters.Clear();
        }
    }

    private static List<char> ExtractLetters(string instance)
    {
        var letters = new List<char>();
        foreach (Match match in LetterPattern.Matches(instance.ToUpperInvariant()))
        {
            var letter = match.Groups[1].Value[0];
            if (!letters.Contains(letter))
            {
                letters.Add(letter);
            }
        }

        return letters;
    }

    /// <summary>
    /// 速率计数器必须复用同一个实例：新建的计数器第一次 NextValue() 永远返回 0，
    /// 它要靠两次采样之间的时间差才能算出速率。
    /// </summary>
    private Counters? GetOrCreate(string instance)
    {
        if (_counters.TryGetValue(instance, out var existing))
        {
            return existing;
        }

        try
        {
            var counters = new Counters(
                new PerformanceCounter(CategoryName, ReadCounter, instance, readOnly: true),
                new PerformanceCounter(CategoryName, WriteCounter, instance, readOnly: true));

            // 先各采一次，把首帧的 0 顶掉
            counters.Read.NextValue();
            counters.Write.NextValue();

            _counters[instance] = counters;
            return counters;
        }
        catch (Exception ex)
        {
            Log.Debug($"创建 {instance} 性能计数器失败: {ex.Message}");
            return null;
        }
    }

    private sealed class Counters(PerformanceCounter read, PerformanceCounter write) : IDisposable
    {
        public PerformanceCounter Read { get; } = read;

        public PerformanceCounter Write { get; } = write;

        public void Dispose()
        {
            Read.Dispose();
            Write.Dispose();
        }
    }
}

/// <summary>整机网络收发速率，所有已启用的物理网卡合计。</summary>
internal sealed class NetworkSpeedProvider
{
    private long _lastSent = -1;
    private long _lastReceived = -1;
    private long _lastTimestamp;

    public (double UploadKbs, double DownloadKbs) Read()
    {
        var now = Environment.TickCount64;

        try
        {
            long sent = 0;
            long received = 0;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var (nicSent, nicReceived) = ReadNicCounters(nic);
                sent += nicSent;
                received += nicReceived;
            }

            var upload = 0.0;
            var download = 0.0;

            if (_lastSent >= 0 && now > _lastTimestamp)
            {
                var elapsed = (now - _lastTimestamp) / 1000.0;
                upload = Math.Max(0, sent - _lastSent) / elapsed / 1024;
                download = Math.Max(0, received - _lastReceived) / elapsed / 1024;
            }

            _lastSent = sent;
            _lastReceived = received;
            _lastTimestamp = now;

            return (Math.Round(upload, 1), Math.Round(download, 1));
        }
        catch (Exception ex)
        {
            Log.Error("读取网络速度失败", ex);
            return (0, 0);
        }
    }

    /// <summary>
    /// 只统计 IPv4。.NET 在 Windows 上没有逐网卡的 IPv6 计数器
    /// （GetIPv6Statistics 在 Windows 目标框架下不可用），
    /// 而 psutil 的 net_io_counters 是 IPv4+IPv6 合计 —— 纯 IPv6 流量的机器上会偏小。
    /// </summary>
    private static (long Sent, long Received) ReadNicCounters(NetworkInterface nic)
    {
        try
        {
            var statistics = nic.GetIPStatistics();
            return (statistics.BytesSent, statistics.BytesReceived);
        }
        catch (Exception ex)
        {
            Log.Debug($"读取 {nic.Name} 网络计数失败: {ex.Message}");
            return (0, 0);
        }
    }
}
