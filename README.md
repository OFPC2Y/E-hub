# e-hub

Windows 系统监控托盘程序：实时采集 CPU / GPU / 内存 / 磁盘 / 网络，通过串口把数据
发给硬件 HUB 的显示屏，并提供一个深色面板和托盘图标。

底层传感器读取用 [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)，
**进程内直接调用**，不需要 pythonnet 之类的托管桥。

## 构建

需要 .NET 8 SDK。

```bash
dotnet build EHub.sln -c Release
```

## 运行

程序清单里带 `requireAdministrator`，启动时会弹 UAC —— CPU 温度、功耗、风扇转速
都要读硬件底层寄存器，没有管理员权限一律拿不到。

```bash
dotnet run --project src/EHub/EHub.csproj
```

## 发布

```bash
dotnet publish src/EHub/EHub.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

产物在 `src/EHub/bin/Release/net8.0-windows/win-x64/publish/`，
`external/PawnIO_setup.exe` 会随包带出，用于首次运行时安装内核驱动。

自包含的 exe 约 136 MB（.NET 运行时 + WPF 都打进去了）。加
`-p:EnableCompressionInSingleFile=true` 可以压到一半左右，代价是启动稍慢。
不需要自包含的话去掉 `--self-contained true`，exe 只有几 MB，
但目标机器要先装 .NET 8 Desktop Runtime。

## 目录结构

```
src/EHub/
  App.xaml(.cs)          启动、崩溃兜底、托盘图标资源
  app.manifest           requireAdministrator + DPI 感知
  Configuration/         配置模型、校验、原子读写（对应原 app_config.py）
  Diagnostics/           滚动日志（对应原 app_log.py）
  Hardware/
    HardwareMonitor.cs   LibreHardwareMonitor 封装 + 传感器关键字匹配（原 lhm_sensors.py）
    TelemetryCollector.cs 汇总一次采集（原 monitor.py）
    Telemetry.cs         采集结果，同时就是串口帧，属性名即对端固件契约
    Providers/           各项指标的读取实现
  Serial/                串口枚举与帧发送（原 serial_sender.py）
  Services/
    MonitorService.cs    采集 / ping 双循环、串口重连、丢帧上报（原 tray_app.py 后台部分）
    AppController.cs     托盘菜单、面板与对话框的装配（原 tray_app.TrayApp）
    PawnIoService.cs     内核驱动检测与安装
  ViewModels/            面板的数据格式化与绑定
  Views/                 主面板、设置、诊断三个窗口
  Themes/                深色配色与控件样式
```

## 运行期文件

| 文件 | 位置 | 说明 |
| --- | --- | --- |
| `config.json` | 与 exe 同目录 | 删掉即恢复默认值 |
| `logs/ehub.log` | 与 exe 同目录 | 超过 512KB 轮转，保留 3 份 |

面向用户的使用说明见 [使用说明.txt](使用说明.txt)。

## 串口帧

发往 HUB 的是一行一个 JSON 对象，键名对端固件解析时即契约，改名等于改协议。
字段与原 Python 版一致，另新增了一个：

| 键 | 说明 |
| --- | --- |
| `volume` / `volume_muted` | 主音量电平，以及是否被静音。静音时 `volume` 不会归零，两个要一起看 |
| `disk_io_<盘符>_read` / `_write` | 每个盘符一条，键名动态生成 |

`serial_error` / `config_error` / `pawnio_message` 只在界面显示，不进帧。

## 注意事项

- `NuGet.config` 显式声明了 `nuget.azure.cn` 镜像源：本机到 `api.nuget.org` 会被重定向，
  不显式配置的话还原阶段可能协商失败。
- `System.IO.Ports` 的版本必须 ≥ LibreHardwareMonitorLib 0.9.6 要求的 10.0.3，
  压到 8.x 会被 NuGet 判定为包降级而还原失败。
- 托盘菜单和弹出菜单用的是自定义 `ControlTemplate`：WPF 默认的 `ContextMenu` 模板
  左侧有一条硬编码浅色的图标留白栏，只设 `Background` 盖不住，会在菜单左边留下白竖条。
