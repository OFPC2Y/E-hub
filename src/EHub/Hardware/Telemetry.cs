using System.Text.Json.Serialization;

namespace EHub.Hardware;

/// <summary>单个磁盘分区的用量快照。</summary>
public sealed class DiskUsage
{
    [JsonPropertyName("mount")]
    public string Mount { get; set; } = "";

    [JsonPropertyName("total")]
    public double Total { get; set; }

    [JsonPropertyName("used")]
    public double Used { get; set; }

    [JsonPropertyName("percent")]
    public double Percent { get; set; }
}

/// <summary>
/// 一次采集的完整结果。这个类型同时也是发往 HUB 的串口帧，
/// 属性名（JsonPropertyName）即对端固件的解析契约，改名等于改协议。
/// </summary>
public sealed class Telemetry
{
    [JsonPropertyName("cpu_model")]
    public string? CpuModel { get; set; }

    [JsonPropertyName("cpu_percent")]
    public double? CpuPercent { get; set; }

    [JsonPropertyName("cpu_temp")]
    public double? CpuTemp { get; set; }

    [JsonPropertyName("cpu_fan_rpm")]
    public double? CpuFanRpm { get; set; }

    [JsonPropertyName("cpu_power")]
    public double? CpuPower { get; set; }

    [JsonPropertyName("gpu_name")]
    public string? GpuName { get; set; }

    [JsonPropertyName("gpu_percent")]
    public double? GpuPercent { get; set; }

    [JsonPropertyName("gpu_temp")]
    public double? GpuTemp { get; set; }

    [JsonPropertyName("gpu_fan_speed")]
    public double? GpuFanSpeed { get; set; }

    [JsonPropertyName("gpu_power")]
    public double? GpuPower { get; set; }

    [JsonPropertyName("gpu_mem_used")]
    public double? GpuMemUsed { get; set; }

    [JsonPropertyName("gpu_mem_total")]
    public double? GpuMemTotal { get; set; }

    [JsonPropertyName("mem_total")]
    public double MemTotal { get; set; }

    [JsonPropertyName("mem_used")]
    public double MemUsed { get; set; }

    [JsonPropertyName("mem_percent")]
    public double MemPercent { get; set; }

    [JsonPropertyName("disks")]
    public List<DiskUsage> Disks { get; set; } = new();

    [JsonPropertyName("net_upload_kbs")]
    public double NetUploadKbs { get; set; }

    [JsonPropertyName("net_download_kbs")]
    public double NetDownloadKbs { get; set; }

    [JsonPropertyName("net_latency_ms")]
    public double? NetLatencyMs { get; set; }

    [JsonPropertyName("ping_target")]
    public string? PingTarget { get; set; }

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }

    /// <summary>主音量是否被静音。静音时 Volume 不会归零，两个值要一起看。</summary>
    [JsonPropertyName("volume_muted")]
    public bool VolumeMuted { get; set; }

    [JsonPropertyName("uptime")]
    public string? Uptime { get; set; }

    [JsonPropertyName("lhm_error")]
    public string? LhmError { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// disk_io_C_read / disk_io_C_write 这类键名带盘符，无法声明成固定属性，
    /// 走扩展数据平铺进 JSON，帧格式与原来完全一致。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?> DiskIo { get; set; } = new();

    // ── 以下只在界面展示，不进串口帧 ──────────────────────────

    [JsonIgnore]
    public string? SerialError { get; set; }

    [JsonIgnore]
    public string? ConfigError { get; set; }

    [JsonIgnore]
    public string? PawnIoMessage { get; set; }

    public Telemetry Clone() => (Telemetry)MemberwiseClone();
}
