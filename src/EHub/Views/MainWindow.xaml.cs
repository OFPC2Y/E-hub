using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using EHub.Diagnostics;
using EHub.Services;
using EHub.ViewModels;

namespace EHub.Views;

public partial class MainWindow : Window
{
    private const int RefreshIntervalMilliseconds = 1000;

    private readonly AppController _controller;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow(AppController controller, MainViewModel viewModel)
    {
        _controller = controller;
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RefreshIntervalMilliseconds),
        };
        _refreshTimer.Tick += OnRefreshTick;

        _viewModel.Refresh();
        _refreshTimer.Start();
    }

    /// <summary>退出流程绕过「关闭即隐藏」，由 AppController 在真正退出前置位。</summary>
    public bool AllowClose { get; set; }

    public void ShowPanel()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ResumeRefresh();
    }

    public void HideToTray()
    {
        // 隐藏时停掉刷新，否则面板不可见还在每秒格式化一遍数据
        StopRefresh();
        Hide();
    }

    public void StopRefresh() => _refreshTimer.Stop();

    public void ResumeRefresh()
    {
        _viewModel.Refresh();
        _refreshTimer.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭按钮等同于「隐藏到托盘」。托盘图标没显示出来时不能只是隐藏：
        // 用户会找不到任何入口，只能去任务管理器结束进程
        if (!AllowClose && _controller.CanHideToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        try
        {
            _viewModel.Refresh();
        }
        catch (Exception ex)
        {
            // 格式化出错不能让定时链断掉，否则面板会永久停在旧数据上
            Log.Error("面板刷新出错", ex);
        }
    }

    private void OnHideToTray(object sender, RoutedEventArgs e) => HideToTray();

    private void OnExit(object sender, RoutedEventArgs e) => _controller.Quit();

    private void OnOpenSettings(object sender, RoutedEventArgs e) => _controller.OpenSettings();

    private void OnOpenDiagnostics(object sender, RoutedEventArgs e) => _controller.OpenDiagnostics();

    private void OnOpenLog(object sender, RoutedEventArgs e) => _controller.OpenLog();

    private void OnAbout(object sender, RoutedEventArgs e) => _controller.ShowAbout();
}
