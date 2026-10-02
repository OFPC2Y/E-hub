using System.Collections.ObjectModel;
using System.Windows.Media;
using EHub.Hardware;
using EHub.Services;
using EHub.Themes;

namespace EHub.ViewModels;

/// <summary>
/// 面板的数据来源。对应原 gui_panel.MonitorPanel._update_ui：
/// 每秒从 MonitorService 取一份快照，再把各项格式化成显示文本。
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly MonitorService _monitor;
    private readonly Dictionary<string, DiskRowViewModel> _rowsByMount = new(StringComparer.OrdinalIgnoreCase);

    private Telemetry _latest = new();

    public MainViewModel(MonitorService monitor) => _monitor = monitor;

    public ObservableCollection<DiskRowViewModel> Disks { get; } = new();

    public bool HasDisks => Disks.Count > 0;

    // ── CPU ─────────────────────────────────────────────────
    public string CpuModel => Coalesce(_latest.CpuModel);

    public string CpuPercent => Display.Percent(_latest.CpuPercent);

    public Brush CpuPercentBrush => Palette.Percent(_latest.CpuPercent);

    public string CpuTemp => Display.Temp(_latest.CpuTemp);

    public string CpuPower => Display.Watt(_latest.CpuPower);

    public string CpuFanRpm => Display.Rpm(_latest.CpuFanRpm);

    // ── GPU ─────────────────────────────────────────────────
    public string GpuName => Coalesce(_latest.GpuName);

    public string GpuPercent => Display.Percent(_latest.GpuPercent);

    public Brush GpuPercentBrush => Palette.Percent(_latest.GpuPercent);

    public string GpuTemp => Display.Temp(_latest.GpuTemp);

    public string GpuFanSpeed => Display.WholePercent(_latest.GpuFanSpeed);

    public string GpuPower => Display.Watt(_latest.GpuPower);

    public string GpuMem
    {
        get
        {
            var used = _latest.GpuMemUsed ?? 0;
            var total = _latest.GpuMemTotal ?? 0;
            if (total <= 0)
            {
                return Display.Na;
            }

            var percent = (int)(used / total * 100);
            return $"{Display.Num0(used)}/{Display.Num0(total)} MB ({percent}%)";
        }
    }

    // ── 内存 ────────────────────────────────────────────────
    public string MemPercent => Display.Percent(_latest.MemPercent);

    public Brush MemPercentBrush => Palette.Percent(_latest.MemPercent);

    public string MemDetail => $"{Display.Num0(_latest.MemUsed)}/{Display.Num0(_latest.MemTotal)} MB";

    // ── 网络 ────────────────────────────────────────────────
    public string NetDownload => $"{Display.Num1(_latest.NetDownloadKbs)} KB/s";

    public string NetUpload => $"{Display.Num1(_latest.NetUploadKbs)} KB/s";

    public string NetLatency => _latest.NetLatencyMs is { } latency
        ? $"{Display.Num1(latency)} ms ({PingTarget})"
        : $"检测中... ({PingTarget})";

    public string PingTarget =>
        string.IsNullOrEmpty(_latest.PingTarget) ? "8.8.8.8" : _latest.PingTarget!;

    // ── 系统 ────────────────────────────────────────────────
    public string Volume => _latest.Volume switch
    {
        null => Display.Na,
        // 静音时电平不会归零：两个都报出来，免得看到「23%」以为没静音
        { } level when _latest.VolumeMuted => $"{Display.Num0(level)}% (静音)",
        { } level => Display.WholePercent(level),
    };

    public string Uptime => Coalesce(_latest.Uptime);

    /// <summary>底部状态栏。串口/配置问题最影响使用，优先显示。</summary>
    public string StatusText
    {
        get
        {
            var parts = new List<string>();
            foreach (var message in new[] { _latest.SerialError, _latest.ConfigError, _latest.PawnIoMessage })
            {
                if (!string.IsNullOrEmpty(message))
                {
                    parts.Add(message!);
                }
            }

            if (parts.Count == 0)
            {
                if (!string.IsNullOrEmpty(_latest.LhmError))
                {
                    parts.Add($"LHM: {_latest.LhmError}");
                }
                else if (_latest.CpuTemp is null && _latest.CpuFanRpm is null)
                {
                    parts.Add("LHM: 未检测到CPU传感器（见「工具 → 诊断信息」）");
                }
            }

            if (_latest.Volume is null)
            {
                parts.Add("音量: 不可用");
            }

            return parts.Count > 0 ? string.Join(" | ", parts) : "就绪";
        }
    }

    /// <summary>面板定时器每秒调一次。</summary>
    public void Refresh()
    {
        _latest = _monitor.GetSnapshot();
        UpdateDisks(_latest.Disks);
        RaiseAllChanged();
    }

    private void UpdateDisks(IReadOnlyList<DiskUsage> disks)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var disk in disks)
        {
            present.Add(disk.Mount);

            if (!_rowsByMount.TryGetValue(disk.Mount, out var row))
            {
                row = new DiskRowViewModel(disk.Mount);
                _rowsByMount[disk.Mount] = row;
                Disks.Add(row);
            }

            var letter = disk.Mount.Length > 0 ? disk.Mount[0] : '\0';
            row.Update(
                $"{Display.Num1(disk.Used)}/{Display.Num1(disk.Total)} GB",
                Display.Percent(disk.Percent),
                Palette.Percent(disk.Percent),
                $"R:{DiskIo($"disk_io_{letter}_read")} KB/s",
                $"W:{DiskIo($"disk_io_{letter}_write")} KB/s");
        }

        for (var index = Disks.Count - 1; index >= 0; index--)
        {
            var mount = Disks[index].Mount;
            if (!present.Contains(mount))
            {
                _rowsByMount.Remove(mount);
                Disks.RemoveAt(index);
            }
        }

        RaisePropertyChanged(nameof(HasDisks));
    }

    /// <summary>键名对不上时按 0 显示，与原版 data.get(key, 0) 一致。</summary>
    private string DiskIo(string key) =>
        _latest.DiskIo.TryGetValue(key, out var raw) && raw is double value
            ? Display.Num1(value)
            : "0";

    private static string Coalesce(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Display.Na : value;
}
