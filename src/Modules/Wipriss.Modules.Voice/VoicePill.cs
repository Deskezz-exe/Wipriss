using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Voice;

/// <summary>
/// The pill under the island while dictation runs, with no words: a red dot (the microphone is open) and
/// sound bars that follow the voice. While the phrase is recognized the bars run as a soft wave; errors
/// show one short line. It never takes focus, so the text goes to the program the owner was typing in.
/// Exists only while dictating; the animations stop with it.
/// </summary>
internal sealed class VoicePill : Window
{
    private const double BarWidth = 3, BarGap = 3, MinBar = 4, MaxBar = 20;
    /// <summary>Centre bars move most, like a voice meter.</summary>
    private static readonly double[] Shape = [0.42, 0.62, 0.82, 1.0, 0.82, 0.62, 0.42];

    private readonly Rectangle[] _bars = new Rectangle[Shape.Length];
    private readonly Ellipse _dot;
    private readonly StackPanel _barsPanel;
    private readonly TextBlock _message;
    private readonly Border _card;
    private readonly Random _random = new();
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _closing;

    public VoicePill()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        Title = "Wipriss · Голос";

        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        _dot = new Ellipse
        {
            Width = 7, Height = 7, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)Application.Current.FindResource("DangerBrush"), Opacity = 0,
        };
        _barsPanel = new StackPanel { Orientation = Orientation.Horizontal, Height = MaxBar, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = BarWidth, Height = MinBar, RadiusX = BarWidth / 2, RadiusY = BarWidth / 2, Fill = accent,
                Margin = new Thickness(i == 0 ? 0 : BarGap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            _barsPanel.Children.Add(_bars[i]);
        }
        _message = new TextBlock
        {
            Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_dot);
        row.Children.Add(_barsPanel);
        row.Children.Add(_message);
        _card = new Border
        {
            Height = 34, MinWidth = 88, Padding = new Thickness(16, 0, 18, 0), CornerRadius = new CornerRadius(17),
            Background = (Brush)Application.Current.FindResource("IslandBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("IslandBorderBrush"), BorderThickness = new Thickness(1),
            Margin = new Thickness(6), Child = row, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.86, 0.86),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
        };
        Content = _card;
        _hide.Tick += (_, _) => FadeOut();
        SizeChanged += (_, _) => Place();
    }

    /// <summary>The engine is starting: quiet bars, no red dot yet (the microphone is not open).</summary>
    public void ShowWaiting()
    {
        ShowBars();
        _dot.BeginAnimation(OpacityProperty, null);
        _dot.Opacity = 0;
        _barsPanel.Opacity = 0.45;
        StopWave();
        Appear();
    }

    /// <summary>The microphone is open.</summary>
    public void ShowListening()
    {
        ShowBars();
        StopWave();
        _barsPanel.Opacity = 1;
        _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        Appear();
    }

    /// <summary>Loudness 0..100, about 10 times a second.</summary>
    public void SetLevel(int level)
    {
        double loud = Math.Clamp(level / 100.0, 0, 1);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < _bars.Length; i++)
        {
            double jitter = 0.72 + _random.NextDouble() * 0.28;
            double height = MinBar + (MaxBar - MinBar) * loud * Shape[i] * jitter;
            _bars[i].BeginAnimation(HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
        }
    }

    /// <summary>The microphone is closed and the phrase is being recognized: a soft running wave.</summary>
    public void ShowRecognizing()
    {
        ShowBars();
        _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        for (int i = 0; i < _bars.Length; i++)
        {
            var wave = new DoubleAnimation(MinBar, MinBar + 9, TimeSpan.FromMilliseconds(380))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease,
                BeginTime = TimeSpan.FromMilliseconds(i * 90),
            };
            _bars[i].BeginAnimation(HeightProperty, wave);
        }
        Appear();
    }

    /// <summary>A short line instead of the bars, then the pill goes away by itself.</summary>
    public void ShowMessage(string text)
    {
        StopWave();
        _dot.BeginAnimation(OpacityProperty, null);
        _dot.Opacity = 0;
        _dot.Visibility = Visibility.Collapsed;
        _barsPanel.Visibility = Visibility.Collapsed;
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        Appear();
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>Fading away: a new dictation needs a new pill.</summary>
    public bool IsClosing => _closing;

    public void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        _hide.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        _card.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(170)));
        _card.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(170)));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.NoActivateToolWindow(this);
        SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopWave();
        _hide.Stop();
        base.OnClosed(e);
    }

    private void ShowBars()
    {
        _hide.Stop();
        _message.Visibility = Visibility.Collapsed;
        _dot.Visibility = Visibility.Visible;
        _barsPanel.Visibility = Visibility.Visible;
    }

    private void StopWave()
    {
        foreach (var bar in _bars)
        {
            bar.BeginAnimation(HeightProperty, null);
            bar.Height = MinBar;
        }
    }

    private void Appear()
    {
        if (_closing) return;
        if (!IsVisible) Show();
        if (Opacity < 1)
        {
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            _card.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = spring });
            _card.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = spring });
        }
    }

    /// <summary>Centred under the island (the top of the main screen).</summary>
    private void Place()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = 40;
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
