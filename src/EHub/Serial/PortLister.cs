using System.Globalization;
using System.IO.Ports;
using EHub.Diagnostics;
using Microsoft.Win32;

namespace EHub.Serial;

/// <summary>
/// 可用串口枚举。
///
/// 必须两层都查：.NET 的 SerialPort.GetPortNames() 和 pyserial 的 comports() 一样，
/// 只通过 SetupAPI 枚举「设备接口」，而虚拟串口驱动（VSerial、com0com 之类）
/// 只往注册表 SERIALCOMM 注册、不暴露设备接口 —— 那些端口照样能正常打开，
/// 但列表里看不到，用户就完全没法选。
/// </summary>
internal static class PortLister
{
    private const string SerialCommKey = @"HARDWARE\DEVICEMAP\SERIALCOMM";

    public static List<string> List()
    {
        var setupApi = new List<string>();
        try
        {
            foreach (var name in SerialPort.GetPortNames())
            {
                var normalized = StripDevicePrefix(name);
                if (normalized.Length > 0)
                {
                    setupApi.Add(normalized);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"SerialPort.GetPortNames 失败: {ex.Message}");
        }

        var registry = ReadRegistry();
        var onlyRegistry = registry
            .Where(port => !setupApi.Contains(port, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (onlyRegistry.Count > 0)
        {
            // SetupAPI 枚举不到、只有注册表能看到 —— 这类端口最容易被漏掉
            Log.Debug($"仅注册表可见的串口: {string.Join(", ", onlyRegistry)}");
        }

        var ports = setupApi
            .Concat(registry)
            .Where(port => port.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(port => port, Comparer<string>.Create(Compare))
            .ToList();

        Log.Debug($"可用串口: {string.Join(", ", ports)}");
        return ports;
    }

    private static List<string> ReadRegistry()
    {
        var ports = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SerialCommKey);
            if (key is null)
            {
                return ports;
            }

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string value && value.Trim().Length > 0)
                {
                    ports.Add(value.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"读取 SERIALCOMM 失败: {ex.Message}");
        }

        return ports;
    }

    /// <summary>.NET Framework 的 GetPortNames 会带 \\.\ 前缀，.NET Core 不会，统一剥掉。</summary>
    private static string StripDevicePrefix(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith(@"\\.\", StringComparison.Ordinal) ? trimmed[4..] : trimmed;
    }

    /// <summary>COM2 要排在 COM10 前面，按字符串排会反。</summary>
    private static int Compare(string left, string right)
    {
        var (leftGroup, leftNumber) = Split(left);
        var (rightGroup, rightNumber) = Split(right);

        var byGroup = leftGroup.CompareTo(rightGroup);
        if (byGroup != 0)
        {
            return byGroup;
        }

        var byNumber = leftNumber.CompareTo(rightNumber);
        return byNumber != 0 ? byNumber : string.CompareOrdinal(left, right);
    }

    private static (int Group, int Number) Split(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (digits.Length > 0
            && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return (0, number);
        }

        return (1, 0);
    }
}
