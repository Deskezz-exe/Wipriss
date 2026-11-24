using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wipriss.Capture;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Capture;

/// <summary>
/// "Screenshot copied" in the bottom-right corner. It never takes focus and hides by itself after a few
/// seconds unless the pointer is on it. Clicking it opens the editor. Left out of captures.
/// </summary>
public partial class ShotToast : Window
{
    private readonly DispatcherTimer _hide;
    private bool _closing;

    internal ShotToast(BitmapSource image, bool onClipboard)
    {
        InitializeComponent();
        Thumb.Source = image;
        if (!onClipboard)
        {
            TitleText.Text = "Буфер обмена занят";
            HintText.Text = "Другая программа держала буфер. Нажмите, чтобы открыть снимок и скопировать его.";
        }

        _hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _hide.Tick += (_, _) => FadeOut();
        MouseEnter += (_, _) => _hide.Stop();
        MouseLeave += (_, _) => { _hide.Interval = TimeSpan.FromSeconds(3); _hide.Start(); };
    }

    public event Action? EditRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.NoActivateToolWindow(this);
        Native.ExcludeFromCapture(this);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Bottom-right of the screen the pointer is on, above the taskbar.
        var (x, y) = ScreenGrabber.CursorPosition();
        var monitors = ScreenGrabber.Monitors();
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? monitors.First(m => m.IsPrimary);
        int width = (int)(ActualWidth * monitor.Scale), height = (int)(ActualHeight * monitor.Scale);
        Native.PlaceInPixels(this, monitor.WorkArea.Right - width - (int)(8 * monitor.Scale), monitor.WorkArea.Bottom - height - (int)(8 * monitor.Scale), width, height);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _hide.Start();
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        EditRequested?.Invoke();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        FadeOut();
    }

    private void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        _hide.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hide.Stop();
        Thumb.Source = null; // the screenshot is gone with the toast unless the editor has it
        base.OnClosed(e);
    }
}
