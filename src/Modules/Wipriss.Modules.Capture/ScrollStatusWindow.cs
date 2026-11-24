using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wipriss.Capture;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Capture;

/// <summary>
/// A small note next to the area while it scrolls. It never takes focus (the page must keep it)
/// and is left out of every capture, so it can't end up in the picture.
/// </summary>
internal sealed class ScrollStatusWindow : Window
{
    private readonly PixelRect _area;
    private readonly TextBlock _height = new();

    public ScrollStatusWindow(PixelRect area)
    {
        _area = area;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = (Brush)FindResource("IslandBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = (FontFamily)FindResource("UiFont");

        _height.Text = "Листаю…";
        _height.FontWeight = FontWeights.SemiBold;
        Content = new StackPanel
        {
            Margin = new Thickness(16, 10, 16, 11),
            Children =
            {
                _height,
                new TextBlock
                {
                    Text = "Пошевелите мышью или нажмите Esc, чтобы остановить",
                    Margin = new Thickness(0, 2, 0, 0),
                    FontSize = 11.5,
                    FontFamily = (FontFamily)FindResource("SmallFont"),
                    Foreground = (Brush)FindResource("SubtleTextBrush"),
                },
            },
        };
    }

    public void ShowHeight(int pixels) =>
        _height.Text = $"Листаю… {pixels.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} px";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.NoActivateToolWindow(this);
        NativeWindowStyle.RoundCorners(this);
        Native.ExcludeFromCapture(this);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Below the area if there is room on its monitor, otherwise inside it at the bottom.
        var monitor = ScreenGrabber.Monitors().FirstOrDefault(m => m.Bounds.Contains(_area.X, _area.Y)) ?? ScreenGrabber.Monitors()[0];
        int width = (int)(ActualWidth * monitor.Scale), height = (int)(ActualHeight * monitor.Scale);
        int x = Math.Clamp(_area.X + (_area.Width - width) / 2, monitor.WorkArea.X, monitor.WorkArea.Right - width);
        int y = _area.Bottom + 12 + height <= monitor.WorkArea.Bottom ? _area.Bottom + 12 : _area.Bottom - height - 12;
        Native.PlaceInPixels(this, x, y, width, height);
    }
}
