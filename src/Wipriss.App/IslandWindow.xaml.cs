using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
    private const double CollapsedWidth = 132, CollapsedHeight = 34;
    private const double ExpandedWidth = 548, ExpandedHeight = 244;

    private readonly SettingsStore _store;
    private readonly DispatcherTimer _expandDelay;
    private readonly DispatcherTimer _collapseDelay;
    private FullscreenWatcher? _fullscreen;
    private bool _expanded;
    private bool _shownByUser = true;
    private bool _fullscreenAppInFront;

    public IslandWindow(SettingsStore store)
    {
        InitializeComponent();
        _store = store;

        // One-shot delays: they run only right after the mouse enters or leaves.
        _expandDelay = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
        _expandDelay.Tick += (_, _) => { _expandDelay.Stop(); SetExpanded(true); };
        _collapseDelay = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseDelay.Tick += (_, _) => { _collapseDelay.Stop(); SetExpanded(false); };

        _store.Changed += OnSettingsChanged;
        RefreshTiles();
    }

    public event Action? SettingsRequested;

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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED)
            Dispatcher.BeginInvoke(PlaceAtTop);
        return IntPtr.Zero;
    }

    private void PlaceAtTop()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private void OnSettingsChanged()
    {
        RefreshTiles();
        UpdateVisibility();
    }

    private void RefreshTiles() =>
        Tiles.ItemsSource = ModuleCatalog.All.Where(m => _store.Current.IsModuleEnabled(m.Id)).ToList();

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

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseDelay.Stop();
        if (!_expanded) _expandDelay.Start();
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandDelay.Stop();
        if (_expanded) _collapseDelay.Start();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(false);
        SettingsRequested?.Invoke();
    }

    private void SetExpanded(bool expand)
    {
        if (_expanded == expand) return;
        _expanded = expand;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(expand ? 220 : 170);

        Pill.CornerRadius = new CornerRadius(expand ? 22 : 17);
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(expand ? ExpandedWidth : CollapsedWidth, duration) { EasingFunction = ease });

        var height = new DoubleAnimation(expand ? ExpandedHeight : CollapsedHeight, duration) { EasingFunction = ease };
        if (!expand)
        {
            height.Completed += (_, _) =>
            {
                if (_expanded) return;
                ExpandedContent.Visibility = Visibility.Collapsed; // nothing left to render while collapsed
                CollapsedContent.Visibility = Visibility.Visible;
            };
        }
        Pill.BeginAnimation(HeightProperty, height);

        if (expand)
        {
            CollapsedContent.Visibility = Visibility.Hidden;
            ExpandedContent.Visibility = Visibility.Visible;
            ExpandedContent.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = TimeSpan.FromMilliseconds(60) });
        }
        else
        {
            ExpandedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(90)));
        }
    }

    private void CollapseNow()
    {
        _expandDelay.Stop();
        _collapseDelay.Stop();
        _expanded = false;
        Pill.BeginAnimation(WidthProperty, null);
        Pill.BeginAnimation(HeightProperty, null);
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        Pill.Width = CollapsedWidth;
        Pill.Height = CollapsedHeight;
        Pill.CornerRadius = new CornerRadius(17);
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Collapsed;
        CollapsedContent.Visibility = Visibility.Visible;
    }
}
