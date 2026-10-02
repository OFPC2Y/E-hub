using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EHub.Configuration;

/// <summary>
/// config.json 的读取与原子写入。
/// 读取是宽容的：任何单个字段坏掉都只回退该字段的默认值，不让整份配置作废。
/// </summary>
public static class ConfigStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>返回 (配置, 错误信息)。文件不存在时静默用默认值，与删除 config.json 即恢复默认的设计一致。</summary>
    public static (AppConfig Config, string Error) Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return (new AppConfig(), "");
        }
        catch (DirectoryNotFoundException)
        {
            return (new AppConfig(), "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return (new AppConfig(), $"配置文件读取失败，已使用默认值: {ex.Message}");
        }

        return Validate(text);
    }

    /// <summary>把外部 JSON 收敛到可用范围，避免坏值（0、负数、字符串）打爆采集循环。</summary>
    public static (AppConfig Config, string Error) Validate(string text)
    {
        var config = new AppConfig();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return (config, "配置文件格式不正确，已使用默认值");
        }

        if (root is not JsonObject obj)
        {
            return (config, "配置文件格式不正确，已使用默认值");
        }

        var port = ReadString(obj, "serial_port");
        if (!string.IsNullOrEmpty(port))
        {
            config.SerialPort = port;
        }

        var target = ReadString(obj, "ping_target");
        if (!string.IsNullOrEmpty(target))
        {
            config.PingTarget = target;
        }

        if (TryReadInt(obj, "baud_rate", out var baud)
            && baud >= AppConfig.MinBaud && baud <= AppConfig.MaxBaud)
        {
            config.BaudRate = baud;
        }

        if (TryReadDouble(obj, "interval_seconds", out var interval) && interval > 0)
        {
            config.IntervalSeconds = Math.Max(AppConfig.MinIntervalSeconds, interval);
        }

        config.AutoStart = ReadBool(obj, "auto_start");

        return (config, "");
    }

    /// <summary>原子写入，避免写一半崩溃把配置弄坏。返回 (成功, 错误信息)。</summary>
    public static (bool Ok, string Error) Save(AppConfig config, string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        var temp = path + ".tmp";

        try
        {
            var obj = new JsonObject
            {
                ["serial_port"] = config.SerialPort,
                ["baud_rate"] = config.BaudRate,
                ["ping_target"] = config.PingTarget,
                ["interval_seconds"] = config.IntervalSeconds,
                ["auto_start"] = config.AutoStart,
            };

            File.WriteAllText(temp, obj.ToJsonString(WriteOptions), Utf8NoBom);
            File.Move(temp, path, overwrite: true);
            return (true, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return (false, $"配置保存失败: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件删不掉不值得打扰用户，主程序继续
        }
    }

    private static string ReadString(JsonObject obj, string key)
    {
        if (obj[key] is not JsonNode node)
        {
            return "";
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => (node.GetValue<string>() ?? "").Trim(),
            JsonValueKind.Number => node.ToJsonString().Trim(),
            _ => "",
        };
    }

    private static bool TryReadInt(JsonObject obj, string key, out int value)
    {
        value = 0;
        if (obj[key] is not JsonNode node)
        {
            return false;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.AsValue().TryGetValue(out value),
            JsonValueKind.String => int.TryParse(
                node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool TryReadDouble(JsonObject obj, string key, out double value)
    {
        value = 0;
        if (obj[key] is not JsonNode node)
        {
            return false;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.AsValue().TryGetValue(out value),
            JsonValueKind.String => double.TryParse(
                node.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool ReadBool(JsonObject obj, string key)
    {
        if (obj[key] is not JsonNode node)
        {
            return false;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => node.AsValue().TryGetValue(out double number) && number != 0,
            _ => false,
        };
    }
}
