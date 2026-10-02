using System.Windows.Media;

namespace EHub.Themes;

/// <summary>
/// 面板配色。沿用原 tkinter 版的一套深色值（Catppuccin Mocha），
/// 定义在 C# 里供 XAML 用 x:Static 引用，避免 XAML 和代码各维护一份。
/// </summary>
public static class Palette
{
    public static SolidColorBrush Background { get; } = Frozen(0x1e, 0x1e, 0x2e);

    public static SolidColorBrush Card { get; } = Frozen(0x2a, 0x2a, 0x3e);

    public static SolidColorBrush Text { get; } = Frozen(0xcd, 0xd6, 0xf4);

    public static SolidColorBrush Subtext { get; } = Frozen(0xa6, 0xad, 0xc8);

    public static SolidColorBrush Accent { get; } = Frozen(0x89, 0xb4, 0xfa);

    public static SolidColorBrush Green { get; } = Frozen(0xa6, 0xe3, 0xa1);

    public static SolidColorBrush Yellow { get; } = Frozen(0xf9, 0xe2, 0xaf);

    public static SolidColorBrush Red { get; } = Frozen(0xf3, 0x8b, 0xa8);

    public static SolidColorBrush Blue { get; } = Frozen(0x74, 0xc7, 0xec);

    public static SolidColorBrush Pink { get; } = Frozen(0xf5, 0xc2, 0xe7);

    /// <summary>对话框输入框底色，比卡片再深一档。</summary>
    public static SolidColorBrush Field { get; } = Frozen(0x18, 0x18, 0x25);

    /// <summary>次级按钮底色。</summary>
    public static SolidColorBrush Muted { get; } = Frozen(0x45, 0x47, 0x5a);

    /// <summary>占用率配色：低于 60% 绿、85% 以下黄、再高红；无数据用次要文字色。</summary>
    public static SolidColorBrush Percent(double? percent) => percent switch
    {
        null => Subtext,
        < 60 => Green,
        < 85 => Yellow,
        _ => Red,
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        // 冻结后没有 Dispatcher 亲和性，后台采集线程也能安全读取
        brush.Freeze();
        return brush;
    }
}
