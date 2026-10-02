using System.Windows;
using EHub.Configuration;
using EHub.Diagnostics;
using EHub.Services;

namespace EHub;

public partial class App : Application
{
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallCrashHandlers();
        Log.Info("==== e-hub 启动 ====");

        var (config, error) = ConfigStore.Load();
        if (error.Length > 0)
        {
            Log.Warn($"配置加载: {error}");
        }

        // 让注册表里的自启项和配置保持一致（exe 换过位置时要跟着更新）
        AutoStart.Apply(config.AutoStart);

        _controller = new AppController(config, error);
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.DisposeOnExit();
        Log.Info("==== e-hub 退出 ====");
        base.OnExit(e);
    }

    /// <summary>
    /// 打包成 exe（无控制台）之后，未处理异常不会打印到任何地方，
    /// 界面停住但日志一片干净会完全查不出原因。
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // 记完就吞掉：原版接管 Tk 回调异常后界面仍继续运行，
            // 一次刷新出错不该把整个程序带走
            Log.Error("界面线程未处理的异常", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("未处理的异常", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察到的任务异常", args.Exception);
            args.SetObserved();
        };
    }
}
