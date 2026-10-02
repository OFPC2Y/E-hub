using System.Globalization;
using EHub.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace EHub.Hardware;

/// <summary>一次扫描里读到的单个传感器。</summary>
public readonly record struct SensorReading(
    string HardwareName,
    HardwareType HardwareType,
    string SubHardware,
    string SensorName,
    SensorType SensorType,
    float Value);

public readonly record struct CpuReading(double? Percent, double? Temp, double? FanRpm, double? Power);

public readonly record struct GpuReading(
    string? Name,
    double? Percent,
    double? Temp,
    double? FanSpeed,
    double? Power,
    double? MemUsed,
    double? MemTotal);

/// <summary>
/// 一项指标怎么从传感器里挑出来。取值和诊断说明共用同一份定义，
/// 免得改了关键字只改一边、两边对不上。
/// </summary>
public sealed record MetricDefinition(
    string Label,
    SensorType Type,
    string[]? Keywords = null,
    HardwareType[]? ExcludeHardwareTypes = null,
    bool FallbackToAnyOfType = false);

/// <summary>某项指标最终取自哪个传感器；没取到时给出原因。</summary>
public sealed record MetricSource(string Label, string? Sensor, string? Reason)
{
    public string Describe() => Sensor is not null ? $"{Label}: {Sensor}" : $"{Label}: 无 —— {Reason}";
}

/// <summary>
/// LibreHardwareMonitor 的进程内封装。
/// 原版靠 pythonnet 反射加载 net472 DLL，现在是托管引用，直接 new Computer() 即可。
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    /// <summary>Computer.Open() 失败后隔一段时间再试，避免每轮采集都重试。</summary>
    private const long RetryAfterMilliseconds = 60_000;

    private static readonly HardwareType[] GpuHardwareTypes =
    {
        HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel,
    };

    private static readonly HardwareType[] NonCpuHardwareTypes =
    {
        HardwareType.Cpu, HardwareType.Motherboard,
    };

    private static readonly MetricDefinition CpuPercentMetric =
        new("CPU 使用率", SensorType.Load, new[] { "cpu total", "total" });

    private static readonly MetricDefinition CpuTempMetric = new(
        "CPU 温度", SensorType.Temperature,
        new[] { "core average", "core max", "package", "tctl", "tdie", "core #", "cpu" },
        GpuHardwareTypes);

    private static readonly MetricDefinition CpuFanMetric = new(
        "CPU 风扇", SensorType.Fan, new[] { "cpu", "processor" }, GpuHardwareTypes,
        FallbackToAnyOfType: true);

    private static readonly MetricDefinition CpuPowerMetric = new(
        "CPU 功耗", SensorType.Power, new[] { "package", "cpu package", "cpu" }, GpuHardwareTypes);

    private static readonly MetricDefinition GpuPercentMetric = new(
        "GPU 使用率", SensorType.Load, new[] { "gpu core", "d3d 3d", "gpu", "d3d" }, NonCpuHardwareTypes);

    private static readonly MetricDefinition GpuTempMetric = new(
        "GPU 温度", SensorType.Temperature,
        new[] { "gpu core", "gpu hotspot", "gpu temperature", "hotspot", "junction", "edge", "gpu" },
        NonCpuHardwareTypes);

    // 必须先按 gpu 关键字匹配，否则主板/机箱风扇会被当成显卡风扇
    private static readonly MetricDefinition GpuFanMetric = new(
        "GPU 风扇", SensorType.Fan, new[] { "gpu" }, NonCpuHardwareTypes, FallbackToAnyOfType: true);

    private static readonly MetricDefinition GpuPowerMetric = new(
        "GPU 功耗", SensorType.Power,
        new[] { "gpu package", "board", "package", "gpu" }, NonCpuHardwareTypes);

    private static readonly MetricDefinition GpuMemUsedMetric = new(
        "GPU 显存已用", SensorType.SmallData,
        new[] { "dedicated memory used", "gpu memory used", "memory used" }, NonCpuHardwareTypes);

    private static readonly MetricDefinition GpuMemTotalMetric = new(
        "GPU 显存总量", SensorType.SmallData,
        new[] { "dedicated memory total", "gpu memory total", "memory total" }, NonCpuHardwareTypes);

    private static readonly MetricDefinition[] AllMetrics =
    {
        CpuPercentMetric, CpuTempMetric, CpuFanMetric, CpuPowerMetric,
        GpuPercentMetric, GpuTempMetric, GpuFanMetric, GpuPowerMetric,
        GpuMemUsedMetric, GpuMemTotalMetric,
    };

    /// <summary>
    /// 当前进程是否以管理员身份运行。
    /// 没有管理员权限时 LHM 读不到 CPU 温度/风扇 —— 诊断里要把这条摆出来，
    /// 否则「整机没有 Temperature 传感器」会被误读成硬件不支持。
    /// </summary>
    public static bool IsElevated { get; } = DetectElevation();

    /// <summary>诊断窗口和采集线程都会扫描，Computer 不是线程安全的。</summary>
    private readonly object _gate = new();

    private Computer? _computer;
    private long _nextOpenAttempt;

    /// <summary>
    /// LibreHardwareMonitor 是否已打开成功。
    /// 首次访问会真正去 Open 一次 —— 不能只看「DLL 在不在」，
    /// 否则调用方会以为不能用就永远不去初始化。
    /// 失败后按 RetryAfterMilliseconds 节流重试。
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return EnsureOpen() is not null;
            }
        }
    }

    public string LastError { get; private set; } = "";

    /// <summary>递归扫描全部硬件传感器。每次全量扫描都很重，调用方应复用结果。</summary>
    public IReadOnlyList<SensorReading> Scan()
    {
        var readings = new List<SensorReading>();

        lock (_gate)
        {
            var computer = EnsureOpen();
            if (computer is null)
            {
                return readings;
            }

            try
            {
                foreach (var hardware in computer.Hardware)
                {
                    Walk(hardware, hardware.Name, hardware.HardwareType, "", readings);
                }

                LastError = "";
            }
            catch (Exception ex)
            {
                LastError = $"传感器扫描异常: {ex.Message}";
                Log.Error("LHM 传感器扫描失败", ex);
            }
        }

        return readings;
    }

    /// <summary>LHM 已加载但完全没有非 GPU 的温度传感器 → 多半缺 PawnIO 内核驱动。</summary>
    public bool IsPawnIoNeeded(IReadOnlyList<SensorReading> sensors)
    {
        if (!IsAvailable)
        {
            return false;
        }

        foreach (var sensor in sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && !IsGpu(sensor.HardwareType))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>「LHM 原始传感器」诊断页用：按硬件分组列出每个传感器。</summary>
    public static IReadOnlyDictionary<string, List<string>> DescribeSensors(IReadOnlyList<SensorReading> sensors)
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var sensor in sensors)
        {
            var key = $"[{sensor.HardwareType}] {sensor.HardwareName}";
            if (sensor.SubHardware.Length > 0)
            {
                key += $" / {sensor.SubHardware}";
            }

            if (!result.TryGetValue(key, out var lines))
            {
                result[key] = lines = new List<string>();
            }

            lines.Add($"  {sensor.SensorName} = "
                      + $"{sensor.Value.ToString("F1", CultureInfo.InvariantCulture)} ({sensor.SensorType})");
        }

        return result;
    }

    /// <summary>
    /// 「指标来源」诊断页用：逐项说明面板上的每个值取自哪个传感器，
    /// 取不到时说明是整机没有这类传感器，还是有关键字没命中。
    /// </summary>
    public static IReadOnlyList<MetricSource> DescribeSources(IReadOnlyList<SensorReading> sensors)
    {
        var list = new List<MetricSource>();

        var gpuName = GpuName(sensors);
        list.Add(new MetricSource(
            "GPU 型号",
            gpuName,
            gpuName is null ? "整机没有 GpuNvidia / GpuAmd / GpuIntel 类型的硬件" : null));

        foreach (var metric in AllMetrics)
        {
            list.Add(Describe(sensors, metric));
        }

        return list;
    }

    /// <summary>把 Computer 内部状态清空，让下一次扫描重新 Open（PawnIO 装完必须走这一步）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                Log.Debug($"Computer.Close 失败: {ex.Message}");
            }

            _computer = null;
            _nextOpenAttempt = 0;
            LastError = "";
        }
    }

    public void Dispose() => Reset();

    public static CpuReading ReadCpu(IReadOnlyList<SensorReading> sensors) => new(
        Round(MatchValue(sensors, CpuPercentMetric), 1),
        Round(MatchValue(sensors, CpuTempMetric), 1),
        Round(MatchValue(sensors, CpuFanMetric), 0),
        Round(MatchValue(sensors, CpuPowerMetric), 1));

    public static GpuReading ReadGpu(IReadOnlyList<SensorReading> sensors) => new(
        GpuName(sensors),
        Round(MatchValue(sensors, GpuPercentMetric), 1),
        Round(MatchValue(sensors, GpuTempMetric), 1),
        Round(MatchValue(sensors, GpuFanMetric), 0),
        Round(MatchValue(sensors, GpuPowerMetric), 1),
        Round(MatchValue(sensors, GpuMemUsedMetric), 0),
        Round(MatchValue(sensors, GpuMemTotalMetric), 0));

    private static string? GpuName(IReadOnlyList<SensorReading> sensors)
    {
        foreach (var sensor in sensors)
        {
            if (IsGpu(sensor.HardwareType))
            {
                return sensor.HardwareName;
            }
        }

        return null;
    }

    private static bool IsGpu(HardwareType type) => Array.IndexOf(GpuHardwareTypes, type) >= 0;

    private static double? Round(float? value, int digits) =>
        value is null ? null : Math.Round((double)value.Value, digits);

    private static float? MatchValue(IReadOnlyList<SensorReading> sensors, MetricDefinition metric)
    {
        var value = SensorMatcher.Match(
            sensors, metric.Type, metric.Keywords, metric.ExcludeHardwareTypes);

        if (value is null && metric.FallbackToAnyOfType)
        {
            value = SensorMatcher.Match(sensors, metric.Type, null, metric.ExcludeHardwareTypes);
        }

        return value;
    }

    private static MetricSource Describe(IReadOnlyList<SensorReading> sensors, MetricDefinition metric)
    {
        var candidates = new List<SensorReading>();
        foreach (var sensor in sensors)
        {
            if (sensor.SensorType != metric.Type)
            {
                continue;
            }

            if (metric.ExcludeHardwareTypes is not null
                && Array.IndexOf(metric.ExcludeHardwareTypes, sensor.HardwareType) >= 0)
            {
                continue;
            }

            candidates.Add(sensor);
        }

        if (candidates.Count == 0)
        {
            var reason = WhyNoCandidates(sensors, metric);
            return new MetricSource(metric.Label, null, reason + ElevationHint(metric));
        }

        var match = MatchValueWithSensor(sensors, metric);
        if (match is null)
        {
            var keywords = metric.Keywords is { Length: > 0 } list ? string.Join(" / ", list) : "(无关键字)";
            var names = string.Join("、", candidates.Select(s => s.SensorName));
            return new MetricSource(
                metric.Label, null,
                $"{candidates.Count} 个 {metric.Type} 传感器都没命中关键字 [{keywords}]：{names}");
        }

        var matched = match.Value.Sensor;
        var nested = matched.SubHardware.Length > 0 ? $" / {matched.SubHardware}" : "";
        var detail = $"{matched.SensorName} = {matched.Value.ToString("F1", CultureInfo.InvariantCulture)}"
                     + $"（{matched.HardwareType}{nested} · {matched.HardwareName}）";

        return new MetricSource(metric.Label, detail, null);
    }

    /// <summary>GPU 那几项的候选集排除了 Cpu/Motherboard，缺失原因要和 CPU 项区分开。</summary>
    private static bool IsGpuSlice(MetricDefinition metric) =>
        metric.ExcludeHardwareTypes is not null
        && metric.ExcludeHardwareTypes.Length == 2
        && Array.IndexOf(metric.ExcludeHardwareTypes, HardwareType.Cpu) >= 0;

    private static string WhyNoCandidates(IReadOnlyList<SensorReading> sensors, MetricDefinition metric)
    {
        if (!IsGpuSlice(metric))
        {
            return $"整机没有 {metric.Type} 类型的传感器";
        }

        // 显卡硬件在、只是没有这一类传感器，和「根本没有独立显卡」是两回事
        foreach (var sensor in sensors)
        {
            if (IsGpu(sensor.HardwareType))
            {
                return $"该 GPU 不提供 {metric.Type} 类型的传感器";
            }
        }

        return "整机没有 GPU 硬件";
    }

    private static string ElevationHint(MetricDefinition metric)
    {
        if (IsElevated || metric.Type is not (SensorType.Temperature or SensorType.Fan))
        {
            return "";
        }

        return "（当前进程未以管理员身份运行，这通常就是原因）";
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Debug($"判断是否提权失败: {ex.Message}");
            return false;
        }
    }

    private static SensorMatch? MatchValueWithSensor(
        IReadOnlyList<SensorReading> sensors, MetricDefinition metric)
    {
        var match = SensorMatcher.MatchSensor(
            sensors, metric.Type, metric.Keywords, metric.ExcludeHardwareTypes);

        return match ?? (metric.FallbackToAnyOfType
            ? SensorMatcher.MatchSensor(sensors, metric.Type, null, metric.ExcludeHardwareTypes)
            : null);
    }

    private Computer? EnsureOpen()
    {
        if (_computer is not null)
        {
            return _computer;
        }

        if (Environment.TickCount64 < _nextOpenAttempt)
        {
            return null;
        }

        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                IsBatteryEnabled = true,
                IsPsuEnabled = true,
            };

            computer.Open();
            _computer = computer;
            LastError = "";
            Log.Info("LibreHardwareMonitor 已就绪");
            return computer;
        }
        catch (Exception ex)
        {
            // 驱动可能是稍后才就绪，隔一段时间再试，不要永久放弃
            LastError = $"Computer.Open 失败: {ex.GetType().Name}: {ex.Message}";
            _nextOpenAttempt = Environment.TickCount64 + RetryAfterMilliseconds;
            Log.Warn("LibreHardwareMonitor 初始化失败", ex);
            return null;
        }
    }

    private static void Walk(
        IHardware hardware, string topName, HardwareType topType, string subPath, List<SensorReading> sink)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            Log.Debug($"硬件 Update 失败 [{hardware.Name}]: {ex.Message}");
        }

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value) || float.IsInfinity(value))
            {
                continue;
            }

            sink.Add(new SensorReading(topName, topType, subPath, sensor.Name, sensor.SensorType, value));
        }

        foreach (var sub in hardware.SubHardware)
        {
            var nested = subPath.Length == 0 ? sub.Name : $"{subPath}/{sub.Name}";
            Walk(sub, topName, topType, nested, sink);
        }
    }
}

internal readonly record struct SensorMatch(float Value, SensorReading Sensor);

/// <summary>
/// 传感器匹配：按关键字优先级取最合适的一个读数。
/// 顺序即优先级，先命中的关键字排前面；没有任何关键字命中就跳过该传感器。
/// </summary>
internal static class SensorMatcher
{
    public static float? Match(
        IReadOnlyList<SensorReading> sensors,
        SensorType type,
        string[]? keywords = null,
        HardwareType[]? excludeHardwareTypes = null) =>
        MatchSensor(sensors, type, keywords, excludeHardwareTypes)?.Value;

    public static SensorMatch? MatchSensor(
        IReadOnlyList<SensorReading> sensors,
        SensorType type,
        string[]? keywords = null,
        HardwareType[]? excludeHardwareTypes = null)
    {
        SensorMatch? best = null;
        var bestPriority = int.MaxValue;

        foreach (var sensor in sensors)
        {
            if (sensor.SensorType != type)
            {
                continue;
            }

            if (excludeHardwareTypes is not null
                && Array.IndexOf(excludeHardwareTypes, sensor.HardwareType) >= 0)
            {
                continue;
            }

            var priority = 99;
            if (keywords is not null)
            {
                var haystack = (sensor.SensorName + " " + sensor.HardwareName + " " + sensor.SubHardware)
                    .ToLowerInvariant();

                for (var i = 0; i < keywords.Length; i++)
                {
                    if (haystack.Contains(keywords[i], StringComparison.Ordinal))
                    {
                        priority = i;
                        break;
                    }
                }

                if (priority == 99)
                {
                    continue;
                }
            }

            // 同优先级保留先扫到的那个
            if (priority < bestPriority)
            {
                bestPriority = priority;
                best = new SensorMatch(sensor.Value, sensor);
            }
        }

        return best;
    }
}
