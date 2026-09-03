using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace ScreenshotCollector;

public partial class EyedropperOverlayWindow : Window
{
    public EyedropperOverlayWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Loaded += (_, _) =>
        {
            Focus();
            RefreshSample();
        };
    }

    public string SelectedColorHex { get; private set; } = "#FFFFFF";

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        RefreshSample();
        var point = e.GetPosition(OverlayCanvas);
        var left = Math.Clamp(point.X + 18, 4, Math.Max(4, ActualWidth - SampleBadge.Width - 4));
        var top = Math.Clamp(point.Y + 18, 4, Math.Max(4, ActualHeight - SampleBadge.Height - 4));
        System.Windows.Controls.Canvas.SetLeft(SampleBadge, left);
        System.Windows.Controls.Canvas.SetTop(SampleBadge, top);
    }

    private void OnOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RefreshSample();
        DialogResult = true;
    }

    private void OnOverlayMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }

    private void RefreshSample()
    {
        var point = Forms.Cursor.Position;
        var color = ReadScreenPixel(point.X, point.Y);
        SelectedColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SampleText.Text = SelectedColorHex;
        SampleSwatch.Background = new SolidColorBrush(color);
    }

    private static Color ReadScreenPixel(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return Colors.White;
        try
        {
            var value = GetPixel(dc, x, y);
            if (value == uint.MaxValue) return Colors.White;
            return Color.FromRgb(
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);
}
