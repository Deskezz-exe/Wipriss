using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wipriss.Capture;

namespace Wipriss.Modules.Capture;

/// <summary>
/// The frozen picture of one monitor, dimmed, with the capture bar. Drag to select an area, or click to take
/// the window under the cursor. Coordinates are converted to the monitor's real pixels, so the result is pixel-exact.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly CaptureSession _session;
    private readonly MonitorInfo _monitor;
    private readonly IReadOnlyList<PixelRect> _windows;
    private Point? _start;
    private bool _dragging;
    private bool _syncingMode;
    private PixelRect _selection;

    internal OverlayWindow(CaptureSession session, MonitorInfo monitor, ScreenImage frozen, IReadOnlyList<PixelRect> windows, bool withToolbar)
    {
        InitializeComponent();
        _session = session;
        _monitor = monitor;
        _windows = windows;
        Frozen = frozen;
        HasToolbar = withToolbar;
        Shot.Source = Images.ToBitmap(frozen);
        Bar.Visibility = withToolbar ? Visibility.Visible : Visibility.Collapsed;
        ModeChanged();
        SizeChanged += (_, _) => ShowArea(default, false);
    }

    internal ScreenImage Frozen { get; }

    internal bool HasToolbar { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var b = _monitor.Bounds;
        Native.PlaceInPixels(this, b.X, b.Y, b.Width, b.Height);
        if (HasToolbar)
        {
            Native.BringToFront(this);
            Activate();
        }
    }

    internal void ModeChanged()
    {
        _syncingMode = true;
        AreaChoice.IsChecked = _session.Mode == CaptureMode.Area;
        ScrollChoice.IsChecked = _session.Mode == CaptureMode.Scroll;
        _syncingMode = false;
        HintText.Text = _session.Mode == CaptureMode.Area
            ? "Выделите область или щёлкните по окну · Esc — отмена"
            : "Выделите то, что прокручивать, или щёлкните по окну: страница пролистается и склеится";
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingMode || _session is null) return; // fires while the XAML loads, before the session is set
        _session.Mode = ReferenceEquals(sender, ScrollChoice) ? CaptureMode.Scroll : CaptureMode.Area;
    }

    private void Screen_Click(object sender, RoutedEventArgs e) => _session.TakeScreen(this);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _session.Cancel();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _session.Cancel();
        base.OnKeyDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Bar.IsMouseOver) return;
        _start = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_start is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && (Math.Abs(point.X - start.X) > 4 || Math.Abs(point.Y - start.Y) > 4)) _dragging = true;
            if (_dragging)
            {
                var a = ToPixels(start);
                var b = ToPixels(point);
                _selection = new PixelRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
                ShowArea(_selection, selecting: true);
            }

            return;
        }

        if (!Bar.IsMouseOver) ShowArea(WindowAt(point), selecting: false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_start is null) return;
        ReleaseMouseCapture();
        var area = _dragging ? _selection : WindowAt(e.GetPosition(this));
        _start = null;
        _dragging = false;
        if (area.Width < 2 || area.Height < 2) return;

        if (_session.Mode == CaptureMode.Area)
            _session.TakeArea(this, area);
        else if (area.Height >= ScrollStitcher.MinOverlap * 2 && area.Width >= 16)
            _session.TakeScroll(new PixelRect(area.X + _monitor.Bounds.X, area.Y + _monitor.Bounds.Y, area.Width, area.Height));
    }

    /// <summary>The topmost window under the point, cut to this monitor (pixels relative to the monitor). The whole monitor if none.</summary>
    private PixelRect WindowAt(Point point)
    {
        var (x, y) = ToPixels(point);
        var b = _monitor.Bounds;
        foreach (var window in _windows)
        {
            if (!window.Contains(x + b.X, y + b.Y)) continue;
            var cut = window.Intersect(b);
            if (!cut.IsEmpty) return new PixelRect(cut.X - b.X, cut.Y - b.Y, cut.Width, cut.Height);
        }

        return new PixelRect(0, 0, Frozen.Width, Frozen.Height);
    }

    private (int X, int Y) ToPixels(Point point)
    {
        double sx = ActualWidth > 0 ? Frozen.Width / ActualWidth : 1, sy = ActualHeight > 0 ? Frozen.Height / ActualHeight : 1;
        return (Math.Clamp((int)Math.Round(point.X * sx), 0, Frozen.Width), Math.Clamp((int)Math.Round(point.Y * sy), 0, Frozen.Height));
    }

    /// <summary>Lights up an area (pixels relative to the monitor) and dims everything else.</summary>
    private void ShowArea(PixelRect area, bool selecting)
    {
        double sx = ActualWidth > 0 ? ActualWidth / Frozen.Width : 1, sy = ActualHeight > 0 ? ActualHeight / Frozen.Height : 1;
        var whole = new Rect(0, 0, ActualWidth, ActualHeight);
        if (area.IsEmpty)
        {
            Dim.Data = new RectangleGeometry(whole);
            Frame.Visibility = SizeTag.Visibility = Visibility.Collapsed;
            return;
        }

        var hole = new Rect(area.X * sx, area.Y * sy, area.Width * sx, area.Height * sy);
        Dim.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { new RectangleGeometry(whole), new RectangleGeometry(hole) } };
        Frame.Margin = new Thickness(hole.X, hole.Y, 0, 0);
        Frame.Width = hole.Width;
        Frame.Height = hole.Height;
        Frame.Stroke = selecting ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE9)) : (Brush)FindResource("AccentBrush");
        Frame.Visibility = Visibility.Visible;

        SizeText.Text = $"{area.Width} × {area.Height}";
        SizeTag.Margin = new Thickness(hole.X, hole.Y >= 28 ? hole.Y - 26 : hole.Y + 6, 0, 0);
        SizeTag.Visibility = Visibility.Visible;
    }
}
