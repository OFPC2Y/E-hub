using EHub.Configuration;
using EHub.Diagnostics;
using EHub.Hardware;
using EHub.Serial;

namespace EHub.Services;

/// <summary>托盘图标颜色，对应原来的绿/红/灰三态。</summary>
public enum TrayState
{
    Gray,
    Green,
    Red,
}

public readonly record struct StatusUpdate(string Title, TrayState? State);

/// <summary>
/// 采集与串口发送的总调度。
/// 对应原 tray_app.TrayApp 里的两个后台循环、串口重连节流和丢帧上报。
/// </summary>
public sealed class MonitorService : IDisposable
{
    private const double PingIntervalSeconds = 5.0;
    private const double DiskRefreshSeconds = 1800;
    private const double ReconnectIntervalSeconds = 5.0;
    private const int ShutdownJoinSeconds = 8;

    private readonly object _stateGate = new();
    private readonly object _dataGate = new();
    private readonly TelemetryCollector _collector = new();
    private readonly SerialSender _sender;

    private AppConfig _config;
    private string _configError;
    private Telemetry _snapshot = new();

    private CancellationTokenSource? _cancellation;
    private Thread? _dataThread;
    private Thread? _pingThread;

    private bool _running;
    private bool _shuttingDown;
    private bool _serialSkipped;
    private bool _connectFailureLogged;
    private long _nextConnectAt;

    public MonitorService(AppConfig config, string configError)
    {
        _config = config;
        _configError = configError;
        _sender = new SerialSender(config.SerialPort, config.BaudRate);
    }

    /// <summary>每轮采集完成后触发，线程为采集线程，订阅方自行切回 UI 线程。</summary>
    public event Action<Telemetry>? SnapshotUpdated;

    public event Action<StatusUpdate>? StatusChanged;

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _running;
            }
        }
    }

    public AppConfig Config => _config;

    public TelemetryCollector Collector => _collector;

    public string SerialPort => _sender.Port;

    public void Start()
    {
        CancellationToken token;

        lock (_stateGate)
        {
            if (_running || _shuttingDown)
            {
                return;
            }

            _running = true;
            _cancellation = new CancellationTokenSource();
            token = _cancellation.Token;

            _dataThread = new Thread(() => DataLoop(token)) { IsBackground = true, Name = "data-loop" };
            _pingThread = new Thread(() => PingLoop(token)) { IsBackground = true, Name = "ping-loop" };
        }

        // 先立刻试一次，成功的话第一帧不用等一个重试周期。
        // 失败也没关系，采集循环会一直重试。放在锁外面做，别让串口 I/O 卡住状态查询
        ConnectSerial(force: true, token);

        _dataThread?.Start();
        _pingThread?.Start();

        Log.Info("监控已启动");
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Thread? dataThread;
        Thread? pingThread;

        lock (_stateGate)
        {
            cancellation = _cancellation;
            dataThread = _dataThread;
            pingThread = _pingThread;

            _running = false;
            _cancellation = null;
            _dataThread = null;
            _pingThread = null;
            _nextConnectAt = 0;
            _connectFailureLogged = false;
            _serialSkipped = false;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (Exception ex)
        {
            Log.Debug($"取消采集循环失败: {ex.Message}");
        }

        // 两个循环都用 token 等事件，Cancel 后立刻返回，join 很快
        JoinQuietly(dataThread, "data-loop");
        JoinQuietly(pingThread, "ping-loop");

        cancellation?.Dispose();

        try
        {
            _sender.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("关闭串口失败", ex);
        }

        if (cancellation is not null)
        {
            RaiseStatus("监控已停止", TrayState.Gray);
        }
    }

    /// <summary>面板每秒读一次，拿到的是一份不会被后台线程改动的快照。</summary>
    public Telemetry GetSnapshot()
    {
        Telemetry copy;
        lock (_dataGate)
        {
            copy = _snapshot.Clone();
        }

        if (_configError.Length > 0)
        {
            copy.ConfigError = _configError;
        }

        return copy;
    }

    public void ApplyConfig(AppConfig newConfig)
    {
        var wasRunning = IsRunning;

        // 只有端口/波特率变了才值得重启采集：其余字段下一个循环就会生效
        var needsRestart = wasRunning
            && (!string.Equals(newConfig.SerialPort, _config.SerialPort, StringComparison.OrdinalIgnoreCase)
                || newConfig.BaudRate != _config.BaudRate);

        _config = newConfig;
        _sender.Port = newConfig.SerialPort;
        _sender.BaudRate = newConfig.BaudRate;

        var (ok, error) = ConfigStore.Save(newConfig);
        _configError = ok ? "" : error;
        if (!ok)
        {
            Log.Warn($"配置保存失败: {error}");
        }

        AutoStart.Apply(newConfig.AutoStart);
        SetSerialError("");

        if (needsRestart)
        {
            // 停/启要 join 采集线程，放到后台做，别卡住设置窗口
            Task.Run(() =>
            {
                Stop();
                Start();
            });
        }
        else
        {
            RaiseStatus("设置已保存", null);
        }
    }

    public void Shutdown()
    {
        lock (_stateGate)
        {
            _shuttingDown = true;
        }

        Stop();
        _collector.Dispose();
        _sender.Dispose();
    }

    public void Dispose() => Shutdown();

    // ── 采集循环 ────────────────────────────────────────────

    private void DataLoop(CancellationToken token)
    {
        List<DiskUsage>? cachedDisks = null;
        var diskRefreshCounter = 0.0;

        while (!token.IsCancellationRequested)
        {
            double interval;
            try
            {
                interval = _config.EffectiveInterval;

                diskRefreshCounter += interval;
                if (cachedDisks is null || diskRefreshCounter >= DiskRefreshSeconds)
                {
                    cachedDisks = _collector.ReadDisks();
                    diskRefreshCounter = 0;
                }

                var telemetry = _collector.Collect(_config, cachedDisks);
                MergeSnapshot(telemetry);

                ConnectSerial(force: false, token);
                SendSerial(token, interval);
            }
            catch (Exception ex)
            {
                Log.Error("数据采集循环出错", ex);
                RaiseStatus($"数据采集出错: {ex.Message}", null);
                interval = _config.EffectiveInterval;
            }

            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(interval)))
            {
                return;
            }
        }
    }

    private void PingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var target = _config.PingTarget;
                var latency = _collector.ReadLatencyAsync(target, token).GetAwaiter().GetResult();

                lock (_dataGate)
                {
                    _snapshot.NetLatencyMs = latency;
                    _snapshot.PingTarget = target;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("延迟采集循环出错", ex);
            }

            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(PingIntervalSeconds)))
            {
                return;
            }
        }
    }

    /// <summary>把新一轮采集并入缓存，保留 ping 线程写入的字段和界面专用的错误信息。</summary>
    private void MergeSnapshot(Telemetry fresh)
    {
        lock (_dataGate)
        {
            fresh.NetLatencyMs = _snapshot.NetLatencyMs;
            if (_snapshot.PingTarget is { Length: > 0 } target)
            {
                fresh.PingTarget = target;
            }

            fresh.SerialError = _snapshot.SerialError;
            fresh.PawnIoMessage = _snapshot.PawnIoMessage;
            _snapshot = fresh;
        }

        SnapshotUpdated?.Invoke(GetSnapshot());
    }

    // ── 串口 ────────────────────────────────────────────────

    /// <summary>
    /// 确保串口是打开的，没打开就按 ReconnectIntervalSeconds 重试。
    /// 设备晚插上、驱动晚加载、被别的程序临时占着，都不该要求用户重启程序。
    /// </summary>
    private void ConnectSerial(bool force, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        lock (_stateGate)
        {
            if (_shuttingDown || !_running)
            {
                return;
            }
        }

        if (_sender.IsOpen)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!force && now < _nextConnectAt)
        {
            return;
        }

        _nextConnectAt = now + (long)(ReconnectIntervalSeconds * 1000);

        try
        {
            _sender.Open();
        }
        catch (Exception ex)
        {
            // 首次失败记 warning（这是排查问题的关键线索），后续重试压到 debug
            if (_connectFailureLogged)
            {
                Log.Debug($"串口重连失败: {ex.Message}");
            }
            else
            {
                _connectFailureLogged = true;
                Log.Warn($"串口打开失败: {ex.Message}");
            }

            SetSerialError($"串口未连接: {ex.Message}（每 {ReconnectIntervalSeconds:0} 秒重试）");
            RaiseStatus($"串口未连接: {_sender.Port}", TrayState.Red);
            return;
        }

        _connectFailureLogged = false;
        Log.Info($"串口已打开: {_sender.Port} @ {_sender.BaudRate}");
        SetSerialError("");
        RaiseStatus($"监控运行中 ({_sender.Port})", TrayState.Green);
    }

    /// <summary>发送合并后的缓存，这样 ping 线程拿到的延迟也会一起发出去。</summary>
    private void SendSerial(CancellationToken token, double interval)
    {
        if (token.IsCancellationRequested || !_sender.IsOpen)
        {
            return;
        }

        var payload = GetSnapshot();

        try
        {
            // 留出 20% 余量，免得刚好卡着间隔
            var budget = interval * 0.8;
            var result = _sender.Send(payload, budget);

            if (result == SendResult.Skipped)
            {
                // 波特率带不动一帧完整数据：丢帧好过写半行把对端解析搞乱
                if (!_serialSkipped)
                {
                    _serialSkipped = true;
                    Log.Warn($"串口帧被丢弃: {_sender.FrameBytes(payload)} 字节在 {budget:0.00} 秒内发不完"
                             + $"（{_sender.BaudRate} 波特率）");

                    var message = $"串口 {_sender.BaudRate} 波特率带不动当前数据量，"
                                  + "已丢弃部分数据（建议与 HUB 固件一起提高波特率）";
                    SetSerialError(message);
                    RaiseStatus(message, null);
                }
            }
            else if (_serialSkipped)
            {
                _serialSkipped = false;
                SetSerialError("");
            }
        }
        catch (Exception ex)
        {
            // 掉线要说出来，不能一直假装在发。断开后交给 ConnectSerial 重连
            Log.Warn($"串口发送失败: {ex.Message}");
            _sender.Close();

            // 端口是刚刚才坏的，下一次循环立刻重试，不用等满一个节流周期
            _nextConnectAt = 0;

            SetSerialError($"串口已断开: {ex.Message}（正在重连）");
            RaiseStatus($"串口已断开: {ex.Message}", TrayState.Red);
        }
    }

    /// <summary>刷新串口列表时顺手把重连节流清零，别让用户再等一个重试周期。</summary>
    public void ResetReconnectThrottle()
    {
        _nextConnectAt = 0;
        _connectFailureLogged = false;
    }

    public void SetPawnIoMessage(string message)
    {
        lock (_dataGate)
        {
            _snapshot.PawnIoMessage = message;
        }
    }

    private void SetSerialError(string message)
    {
        lock (_dataGate)
        {
            _snapshot.SerialError = message;
        }
    }

    private void RaiseStatus(string title, TrayState? state) =>
        StatusChanged?.Invoke(new StatusUpdate(title, state));

    private static void JoinQuietly(Thread? thread, string name)
    {
        if (thread is null || !thread.IsAlive)
        {
            return;
        }

        if (!thread.Join(TimeSpan.FromSeconds(ShutdownJoinSeconds)))
        {
            Log.Warn($"{name} 未在 {ShutdownJoinSeconds} 秒内退出");
        }
    }
}
