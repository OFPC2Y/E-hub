namespace EHub.Configuration;

/// <summary>
/// 采集与串口配置。字段一一对应 config.json 里的 snake_case 键，
/// 文件名与键名都和原 Python 版保持一致，旧配置文件可直接沿用。
/// </summary>
public sealed class AppConfig
{
    /// <summary>一帧完整 JSON 约 880 字节，9600 波特率下要 0.92 秒，占满 1 秒间隔没有余量。</summary>
    public const double MinIntervalSeconds = 0.5;

    public const int MinBaud = 300;
    public const int MaxBaud = 4_000_000;

    public string SerialPort { get; set; } = "COM3";

    public int BaudRate { get; set; } = 115200;

    public string PingTarget { get; set; } = "8.8.8.8";

    public double IntervalSeconds { get; set; } = 1;

    public bool AutoStart { get; set; }

    public double EffectiveInterval =>
        IntervalSeconds > MinIntervalSeconds ? IntervalSeconds : MinIntervalSeconds;

    public AppConfig Clone() => (AppConfig)MemberwiseClone();
}
