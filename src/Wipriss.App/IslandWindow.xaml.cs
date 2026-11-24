using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wipriss.App.Interop;
using Wipriss.App.Shell;
using Wipriss.Core.Modules;
using Wipriss.Core.Settings;

namespace Wipriss.App;

/// <summary>
/// The island: a small pill at the top of the primary screen that grows on hover.
/// Idle cost is zero: no timers run, nothing is redrawn until the mouse comes in.
/// </summary>
public partial class IslandWindow : Window
{
    // The outline inside the Island grid (612 x 268). The spare room around the open shape
    // is for the spring's small overshoot.
    private static readonly Rect CollapsedRect = new(240, 0, 132, 34);
    private static readonly Rect ExpandedRect = new(8, 0, 596, 256);
    private const double CollapsedRadius = 17, ExpandedRadius = 22;
    private const double ContentRestOffset = -6;

    private static readonly IEasingFunction OpenEase = new SpringEase(damping: 0.78, frequency: 7);
    private static readonly IEasingFunction CloseEase = new SpringEase(damping: 1, frequency: 8);
    private static readonly IEasingFunction FadeEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private readonly RectangleGeometry _shape = new(CollapsedRect, CollapsedRadius, CollapsedRadius);
    private readonly SettingsStore _store;
    private readonly ModuleManager _modules;
    private readonly DispatcherTimer _expandDelay;
    private readonly DispatcherTimer _collapseDelay;
    private List<TileItem> _tiles = [];
    private FullscreenWatcher? _fullscreen;
    private bool _expanded;
    private bool _shownByUser = true;
    private bool _fullscreenAppInFront;

    internal IslandWindow(SettingsStore store, ModuleManager modules)
    {
        InitializeComponent();
        _store = store;
        _modules = modules;

        // One shape both fills the island (as a clip) and draws its edge.
        Surface.Clip = _shape;
        Edge.Data = _shape;

        // One-shot delays: they run only right after the mouse enters or leaves.
        _expandDelay = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
        _expandDelay.Tick += (_, _) => { _expandDelay.Stop(); SetExpanded(true); };
        _collapseDelay = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseDelay.Tick += (_, _) => { _collapseDelay.Stop(); SetExpanded(false); };

        _store.Changed += OnSettingsChanged;
        RebuildTiles();
    }

    public event Action? SettingsRequested;

    /// <summary>A built module's tile was clicked.</summary>
    public event Action<string>? ModuleRequested;

    /// <summary>Hidden or shown from the tray menu.</summary>
    public bool IsShownByUser
    {
        get => _shownByUser;
        set { _shownByUser = value; UpdateVisibility(); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: not in Alt+Tab. No-activate: clicking the island never steals focus from your app.
        nint style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

        var source = HwndSource.FromHwnd(hwnd);
        source.AddHook(WndProc);

        _fullscreen = new FullscreenWatcher(source);
        _fullscreen.Changed += inFront =>
        {
            _fullscreenAppInFront = inFront;
            UpdateVisibility();
        };

        PlaceAtTop();
    }

    protected override void OnClosed(EventArgs e)
    {
        _store.Changed -= OnSettingsChanged;
        _fullscreen?.Dispose();
        base.OnClosed(e);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED)
            Dispatcher.BeginInvoke(PlaceAtTop);
        else if (msg == Native.WM_TIMECHANGE)
            _modules.RaiseTimeChanged();
        return IntPtr.Zero;
    }

    private void PlaceAtTop()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private void OnSettingsChanged()
    {
        var enabled = ModuleCatalog.All.Where(m => _store.Current.IsModuleEnabled(m.Id)).Select(m => m.Id);
        if (enabled.SequenceEqual(_tiles.Select(t => t.Info.Id))) UpdateTiles();
        else RebuildTiles();
        UpdateVisibility();
    }

    /// <summary>
    /// Creates the tiles. Only when the set of enabled modules changes. Their captions are filled when the
    /// island opens, so nothing asks modules for work at startup (some summaries start a background check).
    /// </summary>
    private void RebuildTiles()
    {
        _tiles = ModuleCatalog.All
            .Where(m => _store.Current.IsModuleEnabled(m.Id))
            .Select(m => new TileItem(m))
            .ToList();
        Tiles.ItemsSource = _tiles;
        if (_expanded) UpdateTiles();
    }

    /// <summary>Refreshes what each tile says. Runs when the island opens, so the summaries are fresh.</summary>
    private void UpdateTiles()
    {
        foreach (var tile in _tiles)
        {
            if (_modules.Get(tile.Info.Id) is { } module)
                tile.Update(SafeSummary(module), isReady: true);
            else
                // A planned module shows an empty slot, so its stage number says enough.
                tile.Update(_modules.IsBuilt(tile.Info.Id) ? "не запустился" : $"этап {tile.Info.Stage}", isReady: false);
        }
    }

    private static string SafeSummary(IModule module)
    {
        try { return module.Summary; }
        catch (Exception ex) { CrashLog.Write(ex); return ""; }
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TileItem { IsReady: true } tile }) return;
        CollapseNow();
        ModuleRequested?.Invoke(tile.Info.Id);
    }

    private void UpdateVisibility()
    {
        bool show = _shownByUser && !(_fullscreenAppInFront && _store.Current.HideInFullscreen);
        if (show && !IsVisible)
        {
            Show();
        }
        else if (!show && IsVisible)
        {
            CollapseNow();
            Hide();
        }
    }

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseDelay.Stop();
        if (!_expanded) _expandDelay.Start();
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandDelay.Stop();
        if (_expanded) _collapseDelay.Start();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(false);
        SettingsRequested?.Invoke();
    }

    /// <summary>
    /// Opens or closes the island. Only the outline, opacity and an offset are animated:
    /// all of them are drawn by the render thread, so no frame waits for layout.
    /// </summary>
    private void SetExpanded(bool expand)
    {
        if (_expanded == expand) return;
        _expanded = expand;
        if (expand) UpdateTiles();

        var duration = TimeSpan.FromMilliseconds(expand ? 440 : 300);
        var ease = expand ? OpenEase : CloseEase;
        double radius = expand ? ExpandedRadius : CollapsedRadius;

        var outline = new RectAnimation(expand ? ExpandedRect : CollapsedRect, duration) { EasingFunction = ease };
        _shape.BeginAnimation(RectangleGeometry.RectProperty, outline);
        _shape.BeginAnimation(RectangleGeometry.RadiusXProperty, new DoubleAnimation(radius, duration) { EasingFunction = ease });
        _shape.BeginAnimation(RectangleGeometry.RadiusYProperty, new DoubleAnimation(radius, duration) { EasingFunction = ease });

        ExpandedContent.IsHitTestVisible = expand;
        if (expand)
        {
            Fade(CollapsedContent, 0, delayMs: 0, durationMs: 80);
            Fade(ExpandedContent, 1, delayMs: 60, durationMs: 200);
            ExpandedShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = OpenEase });
        }
        else
        {
            Fade(ExpandedContent, 0, delayMs: 0, durationMs: 90);
            ExpandedShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(ContentRestOffset, TimeSpan.FromMilliseconds(200)) { EasingFunction = FadeEase });
            Fade(CollapsedContent, 1, delayMs: 150, durationMs: 140);
        }
    }

    private static void Fade(UIElement element, double to, int delayMs, int durationMs) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = FadeEase,
        });

    private void CollapseNow()
    {
        _expandDelay.Stop();
        _collapseDelay.Stop();
        _expanded = false;

        _shape.BeginAnimation(RectangleGeometry.RectProperty, null);
        _shape.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
        _shape.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
        _shape.Rect = CollapsedRect;
        _shape.RadiusX = _shape.RadiusY = CollapsedRadius;

        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContent.Opacity = 0;
        ExpandedContent.IsHitTestVisible = false;
        ExpandedShift.BeginAnimation(TranslateTransform.YProperty, null);
        ExpandedShift.Y = ContentRestOffset;
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 1;
    }
}

/// <summary>One tile on the open island. Updated in place, so opening the island never rebuilds the tiles.</summary>
public sealed class TileItem(ModuleInfo info) : INotifyPropertyChanged
{
    public ModuleInfo Info { get; } = info;
    public string Glyph => Info.Glyph;
    public string Title => Info.Title;
    public string Description => Info.Description;
    public string Subtitle { get; private set; } = "";
    public bool IsReady { get; private set; }
    public Cursor Cursor => IsReady ? Cursors.Hand : Cursors.Arrow;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(string subtitle, bool isReady)
    {
        if (subtitle != Subtitle)
        {
            Subtitle = subtitle;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
        }

        if (isReady != IsReady)
        {
            IsReady = isReady;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReady)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cursor)));
        }
    }
}
