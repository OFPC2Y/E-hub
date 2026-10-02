using System.Windows;
using System.Windows.Media;

namespace EHub.Themes;

/// <summary>
/// 托盘图标。原版用 Pillow 现画三个彩色圆点，这里用 WPF 的矢量绘制，
/// 不必再依赖 System.Drawing。
/// </summary>
public static class TrayIcons
{
    /// <summary>绿 = 监控正常。</summary>
    public static ImageSource Green { get; } = Circle(0x00, 0xb4, 0x50);

    /// <summary>红 = 串口异常。</summary>
    public static ImageSource Red { get; } = Circle(0xc8, 0x32, 0x32);

    /// <summary>灰 = 未运行。</summary>
    public static ImageSource Gray { get; } = Circle(0x78, 0x78, 0x78);

    private static ImageSource Circle(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();

        var geometry = new EllipseGeometry(new Point(32, 32), 26, 26);
        geometry.Freeze();

        var drawing = new GeometryDrawing(brush, null, geometry);
        drawing.Freeze();

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
