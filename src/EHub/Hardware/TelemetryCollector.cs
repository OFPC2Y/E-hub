using System.Globalization;
using EHub.Configuration;
using EHub.Hardware.Providers;

namespace EHub.Hardware;

/// <summary>
/// 把所有数据源汇成一份 Telemetry。
/// 对应原 monitor.collect_all：LHM 全量扫描很重，一次扫完给 CPU / GPU 共用。
/// </summary>
public sealed class TelemetryCollector : IDisposable
{
    private const int PingTimeoutMilliseconds = 3000;

    private readonly CpuModelProvider _cpuModel = new();
    private readonly MemoryProvider _memory = new();
    private readonly DiskUsageProvider _disks = new();
    private readonly DiskIoProvider _diskIo = new();
    private readonly NetworkSpeedProvider _network = new();
    private readonly VolumeProvider _volume = new();
    private readonly UptimeProvider _uptime = new();
    private readonly PingProvider _ping = new();

    public HardwareMonitor Hardware { get; } = new();

    /// <summary>分区列表变化很慢，调用方按 DISK_REFRESH 周期缓存，不必每秒重查。</summary>
    public List<DiskUsage> ReadDisks() => _disks.Read();

    public Telemetry Collect(AppConfig config, IReadOnlyList<DiskUsage>? cachedDisks)
    {
        // 一次扫完给 CPU / GPU 共用。没打开成功时 Scan 内部会自己尝试并返回空表，
        // 这里不能先判 IsAvailable —— 那会让初始化永远不会被触发
        var sensors = Hardware.Scan();

        var cpu = HardwareMonitor.ReadCpu(sensors);
        var gpu = HardwareMonitor.ReadGpu(sensors);
        var memory = _memory.Read();
        var (upload, download) = _network.Read();
        var volume = _volume.Read();

        var telemetry = new Telemetry
        {
            CpuModel = _cpuModel.Get(),
            CpuPercent = cpu.Percent,
            CpuTemp = cpu.Temp,
            CpuFanRpm = cpu.FanRpm,
            CpuPower = cpu.Power,
            GpuName = gpu.Name,
            GpuPercent = gpu.Percent,
            GpuTemp = gpu.Temp,
            GpuFanSpeed = gpu.FanSpeed,
            GpuPower = gpu.Power,
            GpuMemUsed = gpu.MemUsed,
            GpuMemTotal = gpu.MemTotal,
            MemTotal = memory.TotalMb,
            MemUsed = memory.UsedMb,
            MemPercent = memory.Percent,
            Disks = (cachedDisks ?? ReadDisks()).ToList(),
            NetUploadKbs = upload,
            NetDownloadKbs = download,
            PingTarget = config.PingTarget,
            Volume = volume.Level,
            VolumeMuted = volume.Muted,
            Uptime = _uptime.Read(),
            LhmError = Hardware.LastError,
            Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        };

        foreach (var pair in _diskIo.Read())
        {
            telemetry.DiskIo[pair.Key] = pair.Value;
        }

        return telemetry;
    }

    public Task<double?> ReadLatencyAsync(string target, CancellationToken cancellationToken) =>
        _ping.ReadAsync(target, PingTimeoutMilliseconds, cancellationToken);

    public void Dispose()
    {
        _diskIo.Dispose();
        _volume.Dispose();
        Hardware.Dispose();
    }
}
