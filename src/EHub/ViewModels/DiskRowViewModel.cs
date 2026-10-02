using System.Windows.Media;
using EHub.Themes;

namespace EHub.ViewModels;

/// <summary>
/// 磁盘列表里的一行。控件只建一次，之后原地改文本，
/// 避免每秒重建控件（对应原版 _create_disk_row / _update_disks 的做法）。
/// </summary>
public sealed class DiskRowViewModel(string mount) : ViewModelBase
{
    private string _used = "--";
    private string _percentText = "--";
    private Brush _percentBrush = Palette.Subtext;
    private string _read = "R:--";
    private string _write = "W:--";

    public string Mount { get; } = mount;

    public string Used
    {
        get => _used;
        private set => SetField(ref _used, value);
    }

    public string PercentText
    {
        get => _percentText;
        private set => SetField(ref _percentText, value);
    }

    public Brush PercentBrush
    {
        get => _percentBrush;
        private set => SetField(ref _percentBrush, value);
    }

    public string Read
    {
        get => _read;
        private set => SetField(ref _read, value);
    }

    public string Write
    {
        get => _write;
        private set => SetField(ref _write, value);
    }

    public void Update(string used, string percentText, Brush percentBrush, string read, string write)
    {
        Used = used;
        PercentText = percentText;
        PercentBrush = percentBrush;
        Read = read;
        Write = write;
    }
}
