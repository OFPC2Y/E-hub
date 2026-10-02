using System.Globalization;

namespace EHub.ViewModels;

/// <summary>
/// 界面文本格式化。
///
/// 原版用 Python f-string，浮点数一律用 '.' 作小数点。这里必须显式走
/// InvariantCulture —— 中文区域的当前区域性同样用 '.'，但德语等区域会用 ','
/// 把 "45.0°C" 显示成 "45,0°C"，与串口帧里发的数字对不上。
/// </summary>
internal static class Display
{
    public const string Na = "N/A";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>原版 round(x, 1) 的结果，形如 45.0 / 12.3。</summary>
    public static string Num1(double? value) =>
        value is null ? Na : value.Value.ToString("0.0", Invariant);

    /// <summary>原版 round(x) 或整除的结果，形如 850 / 16384。</summary>
    public static string Num0(double? value) =>
        value is null ? Na : value.Value.ToString("0", Invariant);

    public static string Temp(double? value) =>
        value is null ? Na : $"{value.Value.ToString("0.0", Invariant)}°C";

    public static string Rpm(double? value) =>
        value is null ? Na : $"{value.Value.ToString("0", Invariant)} RPM";

    public static string Percent(double? value) =>
        value is null ? Na : $"{value.Value.ToString("0.0", Invariant)}%";

    public static string WholePercent(double? value) =>
        value is null ? Na : $"{value.Value.ToString("0", Invariant)}%";

    public static string Watt(double? value) =>
        value is null ? Na : $"{value.Value.ToString("0.0", Invariant)} W";
}
