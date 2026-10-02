using System.IO.Ports;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EHub.Diagnostics;
using EHub.Hardware;

namespace EHub.Serial;

/// <summary>一帧发不出去的原因，交给调用方决定怎么提示用户。</summary>
public enum SendResult
{
    Sent,

    /// <summary>波特率带不动这一帧，已主动丢弃（好过写半行把对端 JSON 解析搞乱）。</summary>
    Skipped,
}

/// <summary>把一次采集打包成一行 JSON 写到串口。</summary>
public sealed class SerialSender : IDisposable
{
    private static readonly JsonSerializerOptions FrameOptions = new()
    {
        // Python 那边是 json.dumps(ensure_ascii=False)：中文（如“5小时30分”）必须原样输出，
        // 默认的转义会把它变成 \uXXXX，一帧能膨胀一大截
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private SerialPort? _port;

    public SerialSender(string port, int baudRate)
    {
        Port = port;
        BaudRate = baudRate;
    }

    public string Port { get; set; }

    public int BaudRate { get; set; }

    /// <summary>可用串口列表（SetupAPI + 注册表去重后按序号排序）。</summary>
    public static List<string> ListPorts() => PortLister.List();

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _port is { IsOpen: true };
            }
        }
    }

    public void Open()
    {
        lock (_gate)
        {
            if (_port is { IsOpen: true })
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(Port))
            {
                throw new InvalidOperationException("未配置串口端口");
            }

            CloseCore();

            var port = new SerialPort(Port, BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                // 波特率不够时 write 会一直阻塞，给个上限兜底
                WriteTimeout = 2000,
            };

            port.Open();
            _port = port;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            CloseCore();
        }
    }

    public int FrameBytes(Telemetry telemetry) => Encode(telemetry).Length;

    /// <summary>这一帧在线上要占用多久。</summary>
    public double DrainSeconds(Telemetry telemetry) => Encode(telemetry).Length / BytesPerSecond();

    /// <summary>
    /// 发送一帧。返回 Skipped 表示这一帧在给定预算内发不完，已主动跳过 ——
    /// 主动跳过而不是硬写，是为了避免超时只写出去半行，把对端的 JSON 解析搞乱
    /// （串口没有帧边界，只能靠换行）。
    /// </summary>
    public SendResult Send(Telemetry telemetry, double? budgetSeconds)
    {
        var payload = Encode(telemetry);

        lock (_gate)
        {
            if (_port is not { IsOpen: true } port)
            {
                throw new InvalidOperationException("串口未打开");
            }

            if (budgetSeconds is not null && payload.Length / BytesPerSecond() > budgetSeconds.Value)
            {
                return SendResult.Skipped;
            }

            port.Write(payload, 0, payload.Length);
            port.BaseStream.Flush();
            return SendResult.Sent;
        }
    }

    public void Dispose() => Close();

    /// <summary>串口 8N1：每字节 10 bit。</summary>
    private double BytesPerSecond() => Math.Max(1.0, BaudRate / 10.0);

    private static byte[] Encode(Telemetry telemetry)
    {
        var json = JsonSerializer.Serialize(telemetry, FrameOptions);
        return Encoding.UTF8.GetBytes(json + "\n");
    }

    private void CloseCore()
    {
        var port = _port;
        _port = null;

        if (port is null)
        {
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"关闭串口失败: {ex.Message}");
        }

        try
        {
            port.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"释放串口失败: {ex.Message}");
        }
    }
}
