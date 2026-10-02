using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EHub.Configuration;
using EHub.Diagnostics;
using EHub.Serial;
using EHub.Themes;
using EHub.ViewModels;
using EHub.Views;
using Hardcodet.Wpf.TaskbarNotification;

namespace EHub.Services;

/// <summary>
/// 应用的总装配：托盘图标、面板窗口、后台采集的启停与相互调用。
/// 对应原 tray_app.TrayApp。
/// </summary>
public sealed class AppController
{
    private const int PawnIoCheckDelayMilliseconds = 2000;
    private const int IconResyncMilliseconds = 1500;
    private const int TitleMaxLength = 120;

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly MonitorService _monitor;
    private readonly PawnIoService _pawnIo;
    private readonly MainViewModel _viewModel;
    private readonly TaskbarIcon? _trayIcon;

    private MainWindow? _panel;
    private bool _shuttingDown;
    private ImageSource _iconSource = TrayIcons.Gray;
    private string _iconTitle = "系统监控 - 就绪";

    public AppController(AppConfig config, string configError)
    {
        _monitor = new MonitorService(config, configError);
        _pawnIo = new PawnIoService(_monitor.Collector);
        _viewModel = new MainViewModel(_monitor);

        _monitor.StatusChanged += OnStatusChanged;

        try
        {
            _trayIcon = (TaskbarIcon?)Application.Current.TryFindResource("TrayIcon");
        }
        catch (Exception ex)
        {
            Log.Error("托盘图标初始化失败", ex);
            _trayIcon = null;
        }
    }

    /// <summary>
    /// 托盘图标是否真的可用。不可用时面板的「关闭」等同于退出 ——
    /// 否则用户会找不到任何入口，只能去任务管理器结束进程。
    /// </summary>
    public bool CanHideToTray => !_shuttingDown && _trayIcon is not null;

    public void Start()
    {
        ApplyIcon();
        RefreshTrayMenu();
        _monitor.Start();
        ShowPanel();

        // 图标状态可能被托盘初始化覆盖掉，隔一会儿补一次
        RunOnce(IconResyncMilliseconds, ApplyIcon);

        // 启动阶段先让采集跑起来，PawnIO 检测延后，免得抢资源
        RunOnce(PawnIoCheckDelayMilliseconds, () => _ = CheckPawnIoAsync());

        Log.Info("启动完成");
    }

    // ── 面板 ────────────────────────────────────────────────

    public void ShowPanel()
    {
        if (_panel is null)
        {
            _panel = new MainWindow(this, _viewModel);
            _panel.Closed += (_, _) => _panel = null;
            _panel.Show();
            return;
        }

        _panel.ShowPanel();
    }

    public void HidePanel() => _panel?.HideToTray();

    public void OpenSettings()
    {
        var window = new SettingsWindow(_monitor.Config) { Owner = VisiblePanel() };
        window.ShowDialog();

        if (window.Result is { } updated)
        {
            _monitor.ApplyConfig(updated);
        }
    }

    public void OpenDiagnostics()
    {
        var window = new DiagnosticsWindow(_monitor) { Owner = VisiblePanel() };
        window.ShowDialog();
    }

    /// <summary>面板隐藏时不能拿它当 Owner，否则 CenterOwner 会把对话框放到屏幕外。</summary>
    private Window? VisiblePanel() => _panel is { IsVisible: true } ? _panel : null;

    public void OpenLog()
    {
        if (Log.OpenInShell())
        {
            return;
        }

        MessageBox.Show(
            $"日志文件尚未生成。\n\n位置：{Log.FilePath}",
            "日志",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public void ShowAbout() =>
        MessageBox.Show(
            "e-hub 系统监控面板\n\n"
            + "实时监控 CPU/GPU/内存/磁盘/网络\n"
            + "支持 LibreHardwareMonitor 精确传感器\n"
            + "数据通过串口发送至硬件 HUB",
            "关于 e-hub",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    // ── 托盘菜单 ────────────────────────────────────────────

    private void RefreshTrayMenu()
    {
        if (_trayIcon is null)
        {
            Log.Warn("托盘图标不可用，菜单无法刷新");
            return;
        }

        try
        {
            _trayIcon.ContextMenu = BuildMenu();
        }
        catch (Exception ex)
        {
            Log.Error("构建托盘菜单失败", ex);
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // 托盘菜单挂在资源对象上、不在可视树里，隐式样式查不到，显式套一层
        if (Application.Current?.TryFindResource(typeof(ContextMenu)) is Style menuStyle)
        {
            menu.Style = menuStyle;
        }

        menu.Items.Add(Item("打开面板", ShowPanel));
        menu.Items.Add(NewSeparator());
        menu.Items.Add(Item("开始监控", () => _monitor.Start()));
        menu.Items.Add(Item("停止监控", () => _monitor.Stop()));
        menu.Items.Add(NewSeparator());
        menu.Items.Add(BuildPortMenu());
        menu.Items.Add(Item("刷新串口列表", RefreshPorts));
        menu.Items.Add(Item("查看日志", OpenLog));
        menu.Items.Add(NewSeparator());
        menu.Items.Add(Item("退出", Quit));

        return menu;
    }

    private MenuItem BuildPortMenu()
    {
        var submenu = Item("选择串口", null);

        var ports = SerialSender.ListPorts();
        if (ports.Count == 0)
        {
            submenu.Items.Add(Item("(无可用串口)", null, enabled: false));
            return submenu;
        }

        var current = _monitor.Config.SerialPort;
        foreach (var port in ports)
        {
            var selected = string.Equals(port, current, StringComparison.OrdinalIgnoreCase);
            submenu.Items.Add(Item(selected ? $"✓ {port}" : port, () => SelectPort(port)));
        }

        return submenu;
    }

    private void RefreshPorts()
    {
        RefreshTrayMenu();

        // 刷新端口多半就是因为刚把设备插上，顺手把重连节流清零，别让用户再等一个周期
        _monitor.ResetReconnectThrottle();
    }

    private void SelectPort(string port)
    {
        var wasRunning = _monitor.IsRunning;
        _monitor.Stop();

        var updated = _monitor.Config.Clone();
        updated.SerialPort = port;
        _monitor.ApplyConfig(updated);

        RefreshTrayMenu();

        if (wasRunning)
        {
            // 切换端口后必须重新启动，否则监控会静悄悄地停掉
            _monitor.Start();
        }
        else
        {
            UpdateStatus(new StatusUpdate($"串口已切换: {port}（监控未运行）", TrayState.Gray));
        }
    }

    // ── 状态与图标 ──────────────────────────────────────────

    private void OnStatusChanged(StatusUpdate update) =>
        _dispatcher.InvokeAsync(() => UpdateStatus(update));

    private void UpdateStatus(StatusUpdate update)
    {
        if (update.State is { } state)
        {
            _iconSource = state switch
            {
                TrayState.Green => TrayIcons.Green,
                TrayState.Red => TrayIcons.Red,
                _ => TrayIcons.Gray,
            };
        }

        _iconTitle = update.Title.Length > TitleMaxLength
            ? update.Title[..TitleMaxLength]
            : update.Title;

        ApplyIcon();
    }

    private void ApplyIcon()
    {
        var tray = _trayIcon;
        if (tray is null || _shuttingDown)
        {
            return;
        }

        try
        {
            tray.IconSource = _iconSource;
            tray.ToolTipText = _iconTitle;
        }
        catch (Exception ex)
        {
            Log.Debug($"更新托盘图标失败: {ex.Message}");
        }
    }

    // ── PawnIO ──────────────────────────────────────────────

    private async Task CheckPawnIoAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        if (!_monitor.Collector.Hardware.IsAvailable)
        {
            // 静默跳过的话，CPU 数据缺失时完全查不出原因
            var reason = _monitor.Collector.Hardware.LastError;
            Log.Warn($"pawnio check: LHM 未加载，跳过（{(reason.Length > 0 ? reason : "无错误信息")}）");
            return;
        }

        bool needed;
        try
        {
            needed = await Task.Run(() => _pawnIo.IsNeeded());
        }
        catch (Exception ex)
        {
            Log.Error("PawnIO 检测失败", ex);
            return;
        }

        Log.Info($"pawnio check: needed={needed}");
        if (needed)
        {
            PromptPawnIo();
        }
    }

    private void PromptPawnIo()
    {
        if (_shuttingDown)
        {
            Log.Info("pawnio check: 已开始退出，跳过弹窗");
            return;
        }

        var answer = MessageBox.Show(
            "LibreHardwareMonitor 已加载，\n"
            + "但 CPU 温度/风扇传感器数据缺失。\n\n"
            + "原因：ASUS B760M 等主板的 SuperIO 芯片\n"
            + "需要 PawnIO 内核驱动才能读取传感器数据。\n\n"
            + "是否立即运行 PawnIO 驱动安装程序？\n"
            + @"（安装路径：external\PawnIO_setup.exe）",
            "安装 PawnIO 内核驱动",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _monitor.SetPawnIoMessage("正在安装 PawnIO 驱动，请稍候...");
        _ = InstallPawnIoAsync();
    }

    private async Task InstallPawnIoAsync()
    {
        var result = await Task.Run(() => _pawnIo.RunSetup());

        _monitor.SetPawnIoMessage(result.Message);

        if (result.Success)
        {
            UpdateStatus(new StatusUpdate("PawnIO 驱动已安装 - 系统监控", null));
            ShowBox("安装成功", result.Message, MessageBoxImage.Information);
            return;
        }

        if (result.NeedsRestart)
        {
            UpdateStatus(new StatusUpdate("PawnIO 需重启程序", TrayState.Red));
            ShowBox("需要重启程序", result.Message, MessageBoxImage.Warning);
            return;
        }

        UpdateStatus(new StatusUpdate("PawnIO 安装失败 - 系统监控", TrayState.Red));
        ShowBox(
            "安装失败",
            $"{result.Message}\n\n"
            + "你也可以手动安装：\n"
            + @"1. 找到 external\PawnIO_setup.exe"
            + "\n2. 右键 → 以管理员身份运行"
            + "\n3. 完成后重启本程序",
            MessageBoxImage.Error);
    }

    // ── 生命周期 ────────────────────────────────────────────

    public void Quit()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        Log.Info("开始退出");

        DisposeTray();

        // 停采集要 join 后台线程，最多可能等十几秒，不能卡住界面线程
        Task.Run(() =>
        {
            _monitor.Shutdown();
            _dispatcher.InvokeAsync(() =>
            {
                if (_panel is not null)
                {
                    _panel.StopRefresh();
                    _panel.AllowClose = true;
                    _panel.Close();
                }

                Application.Current.Shutdown();
            });
        });
    }

    /// <summary>
    /// 进程被外部结束（注销、关机）时兜底清理。正常退出路径已经清理过，
    /// 这里靠 _shuttingDown 保证不会重复执行。
    /// </summary>
    public void DisposeOnExit()
    {
        if (_shuttingDown)
        {
            DisposeTray();
            return;
        }

        _shuttingDown = true;
        DisposeTray();
        _monitor.Shutdown();
    }

    private void DisposeTray()
    {
        try
        {
            _trayIcon?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"释放托盘图标失败: {ex.Message}");
        }
    }

    private void ShowBox(string title, string message, MessageBoxImage icon) =>
        _dispatcher.InvokeAsync(() => MessageBox.Show(
            _panel ?? (Window?)Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            icon));

    private void RunOnce(int delayMilliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("延迟任务执行失败", ex);
            }
        };
        timer.Start();
    }

    private static MenuItem Item(string header, Action? action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };

        // 托盘菜单不在可视树里，隐式样式取不到，这里显式套用深色模板
        if (Application.Current?.TryFindResource(typeof(MenuItem)) is Style style)
        {
            item.Style = style;
        }

        if (action is not null)
        {
            item.Click += (_, _) => action();
        }

        return item;
    }

    /// <summary>同理，分隔符也要显式套样式，否则会画成系统的浅灰线。</summary>
    private static Separator NewSeparator()
    {
        var separator = new Separator();

        if (Application.Current?.TryFindResource(typeof(Separator)) is Style style)
        {
            separator.Style = style;
        }

        return separator;
    }
}
