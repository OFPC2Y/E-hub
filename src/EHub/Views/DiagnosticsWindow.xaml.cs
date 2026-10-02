using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EHub.Diagnostics;
using EHub.Hardware;
using EHub.Services;

namespace EHub.Views;

public partial class DiagnosticsWindow : Window
{
    private static readonly JsonSerializerOptions RawOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly MonitorService _monitor;

    public DiagnosticsWindow(MonitorService monitor)
    {
        _monitor = monitor;
        InitializeComponent();

        var snapshot = monitor.GetSnapshot();

        // 全量扫描很重，两个页签共用这一次结果
        var sensors = monitor.Collector.Hardware.Scan();

        LhmText.Text = BuildSensorText(sensors);
        OverviewText.Text = BuildOverviewText(snapshot, sensors);
        RawJsonText.Text = BuildRawJson(snapshot);
    }

    private string BuildSensorText(IReadOnlyList<SensorReading> sensors)
    {
        var builder = new StringBuilder();

        try
        {
            var categories = HardwareMonitor.DescribeSensors(sensors);
            if (categories.Count == 0)
            {
                builder.AppendLine("LHM 未检测到任何传感器");
                builder.AppendLine($"LHM 错误: {HardwareError()}");
            }

            foreach (var category in categories)
            {
                builder.AppendLine();
                builder.AppendLine(category.Key);
                foreach (var line in category.Value)
                {
                    builder.AppendLine(line);
                }
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"读取失败: {ex.Message}");
            builder.AppendLine($"LHM 状态: {_monitor.Collector.Hardware.IsAvailable}");
            builder.AppendLine($"LHM 错误: {HardwareError()}");
        }

        return builder.ToString();
    }

    private static string BuildOverviewText(Telemetry data, IReadOnlyList<SensorReading> sensors)
    {
        var builder = new StringBuilder();

        builder.AppendLine("【CPU】");
        builder.AppendLine($"  型号:   {Value(data.CpuModel)}");
        builder.AppendLine($"  使用率: {Val(data.CpuPercent, "%")}");
        builder.AppendLine($"  温度:   {Val(data.CpuTemp, "°C")}");
        builder.AppendLine($"  功耗:   {Val(data.CpuPower, " W")}");
        builder.AppendLine($"  风扇:   {Val(data.CpuFanRpm, " RPM")}");
        builder.AppendLine();

        builder.AppendLine("【GPU】");
        builder.AppendLine($"  型号:   {Value(data.GpuName)}");
        builder.AppendLine($"  使用率: {Val(data.GpuPercent, "%")}");
        builder.AppendLine($"  温度:   {Val(data.GpuTemp, "°C")}");
        builder.AppendLine(data.GpuMemTotal is > 0
            ? $"  显存:   {Val(data.GpuMemUsed, string.Empty)} / {Num(data.GpuMemTotal)} MB"
            : "  显存:   N/A");
        builder.AppendLine($"  功耗:   {Val(data.GpuPower, " W")}");
        builder.AppendLine($"  风扇:   {Val(data.GpuFanSpeed, "%")}");
        builder.AppendLine();

        builder.AppendLine("【内存】");
        builder.AppendLine($"  使用率: {Val(data.MemPercent, "%")}");
        builder.AppendLine($"  已用/总量: {Num(data.MemUsed)} / {Num(data.MemTotal)} MB");
        builder.AppendLine();

        builder.AppendLine("【磁盘】");
        if (data.Disks.Count == 0)
        {
            builder.AppendLine("  (无)");
        }
        else
        {
            foreach (var disk in data.Disks)
            {
                var letter = disk.Mount.Length > 0 ? disk.Mount[0] : '\0';
                builder.AppendLine(
                    $"  {disk.Mount}  {Num(disk.Used)}/{Num(disk.Total)} GB  ({Val(disk.Percent, "%")})"
                    + $"  R:{LookupIo(data, $"disk_io_{letter}_read")} KB/s"
                    + $"  W:{LookupIo(data, $"disk_io_{letter}_write")} KB/s");
            }
        }

        // 键名对不上时磁盘速度会恒为 0，这里把实际收到的键列出来便于核对
        var ioKeys = data.DiskIo.Keys.Where(key => key.StartsWith("disk_io_", StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        builder.AppendLine($"  收到的 IO 键: {(ioKeys.Count > 0 ? string.Join(", ", ioKeys) : "(无)")}");
        builder.AppendLine();

        builder.AppendLine("【网络】");
        builder.AppendLine($"  下载: {Val(data.NetDownloadKbs, " KB/s")}");
        builder.AppendLine($"  上传: {Val(data.NetUploadKbs, " KB/s")}");
        builder.AppendLine($"  延迟: {Val(data.NetLatencyMs, " ms")}");
        builder.AppendLine($"  目标: {Value(data.PingTarget)}");
        builder.AppendLine();

        builder.AppendLine("【系统】");
        builder.AppendLine($"  音量:   {Val(data.Volume, "%")}{(data.VolumeMuted ? "（已静音）" : "")}");
        builder.AppendLine($"  运行时间: {Value(data.Uptime)}");
        builder.AppendLine($"  时间戳: {Value(data.Timestamp)}");
        builder.AppendLine();

        builder.AppendLine("【状态】");
        builder.AppendLine($"  串口: {OrOk(data.SerialError)}");
        builder.AppendLine($"  配置: {OrOk(data.ConfigError)}");
        builder.AppendLine($"  LHM:  {OrOk(data.LhmError)}");
        builder.AppendLine($"  权限: {(HardwareMonitor.IsElevated ? "管理员" : "普通用户 —— 读不到 CPU 温度 / 风扇")}");
        builder.AppendLine($"  日志: {Log.FilePath}");
        builder.AppendLine();

        // 面板上每个值到底取自哪个传感器；显示 N/A 时看这里就知道是硬件没有还是没匹配上
        builder.AppendLine("【指标来源】");
        foreach (var source in HardwareMonitor.DescribeSources(sensors))
        {
            builder.AppendLine($"  {source.Describe()}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Raw JSON 页展示的是实际采集内容。串口帧里不含的三个界面字段
    /// （serial_error / config_error / pawnio_message）在这里补上，方便排查。
    /// </summary>
    private static string BuildRawJson(Telemetry data)
    {
        var node = JsonSerializer.SerializeToNode(data, RawOptions) as JsonObject ?? new JsonObject();
        node["serial_error"] = data.SerialError ?? "";
        node["config_error"] = data.ConfigError ?? "";
        node["pawnio_message"] = data.PawnIoMessage ?? "";
        return node.ToJsonString(RawOptions);
    }

    private string HardwareError()
    {
        var error = _monitor.Collector.Hardware.LastError;
        return error.Length > 0 ? error : "无";
    }

    private static string LookupIo(Telemetry data, string key) =>
        data.DiskIo.TryGetValue(key, out var raw) && raw is double value ? Num(value) : "0";

    private static string Val(double? value, string unit) => value is null ? "N/A" : $"{Num(value)}{unit}";

    private static string Num(double? value) =>
        value is null ? "N/A" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Value(string? text) => string.IsNullOrWhiteSpace(text) ? "N/A" : text;

    private static string OrOk(string? text) => string.IsNullOrEmpty(text) ? "正常" : text;

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (!Log.OpenInShell())
        {
            MessageBox.Show(
                this,
                $"日志文件尚未生成。\n\n位置：{Log.FilePath}",
                "日志",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
