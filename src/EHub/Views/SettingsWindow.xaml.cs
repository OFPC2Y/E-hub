using System.Globalization;
using System.Windows;
using EHub.Configuration;

namespace EHub.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();

        ConfigPathText.Text = $"配置文件：{AppPaths.BaseDirectory}";
        IntervalHint.Text = $"最小 {AppConfig.MinIntervalSeconds.ToString(CultureInfo.InvariantCulture)}";

        Apply(config);
    }

    /// <summary>用户点「保存」时才有值，取消或关闭窗口保持 null。</summary>
    public AppConfig? Result { get; private set; }

    private void Apply(AppConfig config)
    {
        PingTargetBox.Text = config.PingTarget;
        SerialPortBox.Text = config.SerialPort;
        BaudRateBox.Text = config.BaudRate.ToString(CultureInfo.InvariantCulture);
        IntervalBox.Text = config.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        AutoStartBox.IsChecked = config.AutoStart;
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e) => Apply(new AppConfig());

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var defaults = new AppConfig();

        // 留空表示沿用默认值，填了就必须是数字
        if (!TryParseBaud(BaudRateBox.Text, defaults.BaudRate, out var baudRate)
            || !TryParseInterval(IntervalBox.Text, defaults.IntervalSeconds, out var interval))
        {
            Fail("波特率和采集间隔必须是数字");
            return;
        }

        if (baudRate < AppConfig.MinBaud || baudRate > AppConfig.MaxBaud)
        {
            Fail($"波特率需要在 {AppConfig.MinBaud}–{AppConfig.MaxBaud} 之间");
            return;
        }

        if (interval <= 0)
        {
            Fail("采集间隔必须大于 0");
            return;
        }

        Result = new AppConfig
        {
            PingTarget = Or(PingTargetBox.Text, defaults.PingTarget),
            SerialPort = Or(SerialPortBox.Text, defaults.SerialPort),
            BaudRate = baudRate,
            IntervalSeconds = Math.Max(AppConfig.MinIntervalSeconds, interval),
            AutoStart = AutoStartBox.IsChecked == true,
        };

        Close();
    }

    private static bool TryParseBaud(string text, int fallback, out int value)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            value = fallback;
            return true;
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseInterval(string text, double fallback, out double value)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            value = fallback;
            return true;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Or(string text, string fallback)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 ? trimmed : fallback;
    }

    private void Fail(string message) =>
        MessageBox.Show(this, message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
}
