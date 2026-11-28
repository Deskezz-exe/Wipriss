using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wipriss.App.Interop;
using Wipriss.App.Shell;
using Wipriss.Core.Modules;
using Wipriss.Core.Settings;
using Wipriss.Core.Ui;

namespace Wipriss.App;

/// <summary>
/// The island: a small pill at the top of the primary screen that grows on hover.
/// Idle cost is zero: no timers run, nothing is redrawn until the mouse comes in.
/// </summary>
public partial class IslandWindow : Window
{
    // The outline inside the Island grid (612 x 294). The spare room around the open shape
    // is for the spring's small overshoot. The closed pill grows to fit what it says.
    private const double IslandWidth = 612, PillHeight = 34, PillMinWidth = 120, PillMaxWidth = 580;
    private const double PillPadding = 14, PillGap = 10, PillGroupGap = 21;

    // The window is only as tall as the pill while closed: a transparent window is redrawn whole, so every
    // figure that changes once a second costs in proportion to the window's area.
    private const double OpenWindowHeight = 304, ClosedWindowHeight = 6 + PillHeight + 6;
    private static readonly Rect ExpandedRect = new(8, 0, 596, 284);
    private const double CollapsedRadius = 17, ExpandedRadius = 22;
    private const double ContentRestOffset = -6;

    private static readonly IEasingFunction OpenEase = new SpringEase(damping: 0.78, frequency: 7);
    private static readonly IEasingFunction CloseEase = new SpringEase(damping: 1, frequency: 8);
    private static readonly IEasingFunction FadeEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction WidthEase = Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut });

    private Rect _collapsedRect = PillRect(PillMinWidth);
    private readonly RectangleGeometry _shape;
    private readonly DispatcherTimer _clock;
    private readonly List<IIslandCard> _cards = [];

    // The closed pill
    private readonly List<IPillSource> _sources = [];
    private readonly List<(IPillSource Source, Action<PillPeek> Handler)> _peekHandlers = [];
    private readonly Dictionary<string, FrameworkElement> _views = [];
    private readonly HashSet<IPillSource> _onPill = [];
    private readonly Dictionary<IPillSource, bool> _shown = [];
    private readonly List<PillPeek> _peekQueue = [];
    private readonly DispatcherTimer _peekTimer;
    private double _restWidth = PillMinWidth;
    private bool _peeking;
    private bool _screenOn = true, _unlocked = true;
    private ScreenStateWatcher? _screenState;
    private HwndSource? _source;
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
        Height = ClosedWindowHeight;
        _store = store;
        _modules = modules;
        _shape = new RectangleGeometry(_collapsedRect, CollapsedRadius, CollapsedRadius);

        // One shape both fills the island (as a clip) and draws its edge.
        Surface.Clip = _shape;
        Edge.Data = _shape;

        // One-shot delays: they run only right after the mouse enters or leaves.
        _expandDelay = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
        _expandDelay.Tick += (_, _) => { _expandDelay.Stop(); SetExpanded(true); };
        _collapseDelay = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseDelay.Tick += (_, _) => { _collapseDelay.Stop(); SetExpanded(false); };

        // Runs only while the island is open: the clock moves on to the next minute.
        _clock = new DispatcherTimer(DispatcherPriority.Background);
        _clock.Tick += (_, _) => UpdateClock();

        // Runs only while a peek is on: it ends the peek.
        _peekTimer = new DispatcherTimer(DispatcherPriority.Background);
        _peekTimer.Tick += (_, _) => ShowNextPeek();

        _store.Changed += OnSettingsChanged;
        IsVisibleChanged += (_, _) => UpdateShown();
        RebuildTiles();
        HookCards();
        HookPill();
    }

    /// <summary>WM_HOTKEY ids go here (set by the app's hotkey service).</summary>
    public Func<int, bool>? HotkeyHandler { get; set; }

    public IntPtr Handle { get; private set; }

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
        Handle = hwnd;
        // Tool window: not in Alt+Tab. No-activate: clicking the island never steals focus from your app.
        nint style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        // The island never shows up in screenshots or screen recordings, like a hardware cut-out.
        // WIPRISS_UI_TEST=1 (checks of the interface only) leaves it visible to screenshots.
        if (Environment.GetEnvironmentVariable("WIPRISS_UI_TEST") != "1")
            Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);

        var source = HwndSource.FromHwnd(hwnd);
        source.AddHook(WndProc);
        _source = source;
        SetRenderMode(expanded: false);

        _fullscreen = new FullscreenWatcher(source);
        _fullscreen.Changed += inFront =>
        {
            _fullscreenAppInFront = inFront;
            UpdateVisibility();
        };
        _screenState = new ScreenStateWatcher(source);
        _screenState.Changed += OnScreenState;

        PlaceAtTop();
    }

    protected override void OnClosed(EventArgs e)
    {
        _store.Changed -= OnSettingsChanged;
        foreach (var card in _cards) card.Changed -= OnCardChanged;
        foreach (var (source, handler) in _peekHandlers)
        {
            source.PillChanged -= RebuildPill;
            source.PillPeek -= handler;
        }
        _clock.Stop();
        _peekTimer.Stop();
        _fullscreen?.Dispose();
        _screenState?.Dispose();
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
        else if (msg == Native.WM_HOTKEY && HotkeyHandler?.Invoke((int)wParam) == true)
            handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// The small closed pill is drawn by the processor: for a transparent window the video card's frame has to be
    /// copied back every time, which costs more than drawing a few figures. The open island animates on the video card.
    /// </summary>
    private void SetRenderMode(bool expanded)
    {
        if (_source?.CompositionTarget is { } target)
            target.RenderMode = expanded ? RenderMode.Default : RenderMode.SoftwareOnly;
    }

    private void PlaceAtTop()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private static IEnumerable<ModuleInfo> DockModules(AppSettings settings) =>
        ModuleCatalog.All.Where(m => m.Place == ModulePlace.Dock && settings.IsModuleEnabled(m.Id));

    private void OnSettingsChanged()
    {
        var enabled = DockModules(_store.Current).Select(m => m.Id);
        if (enabled.SequenceEqual(_tiles.Select(t => t.Info.Id))) UpdateTiles();
        else RebuildTiles();
        HookCards();
        HookPill();
        UpdateVisibility();
    }

    // ---- Live cards and the pill's status ----

    /// <summary>Follows the card modules that are running now (modules start and stop with settings).</summary>
    private void HookCards()
    {
        foreach (var card in _cards) card.Changed -= OnCardChanged;
        _cards.Clear();
        foreach (var info in ModuleCatalog.All.Where(m => m.Place == ModulePlace.Card))
            if (_modules.Get(info.Id) is IIslandCard card)
            {
                _cards.Add(card);
                card.Changed += OnCardChanged;
            }
        OnCardChanged();
    }

    private void OnCardChanged()
    {
        if (_expanded) BuildCards();
    }

    // ---- The closed pill: blocks from modules ----

    /// <summary>Follows the modules that put blocks into the pill.</summary>
    private void HookPill()
    {
        var next = ModuleCatalog.All.Select(m => _modules.Get(m.Id)).OfType<IPillSource>().ToList();
        if (next.SequenceEqual(_sources)) { RebuildPill(); return; }

        foreach (var (source, handler) in _peekHandlers)
        {
            source.PillChanged -= RebuildPill;
            source.PillPeek -= handler;
        }
        _peekHandlers.Clear();
        _sources.Clear();
        _sources.AddRange(next);
        foreach (var source in _sources)
        {
            Action<PillPeek> handler = peek => OnPeek(peek);
            _peekHandlers.Add((source, handler));
            source.PillChanged += RebuildPill;
            source.PillPeek += handler;
        }
        // Stopped modules no longer hear anything from us.
        foreach (var gone in _shown.Keys.Where(s => !_sources.Contains(s)).ToList()) _shown.Remove(gone);
        RebuildPill();
    }

    /// <summary>
    /// Lays the blocks out: grouped left to right, the lowest priority leaves whole while they do not fit,
    /// and the pill takes their width. Views are kept while their slot stays, so nothing is rebuilt needlessly.
    /// </summary>
    private void RebuildPill()
    {
        var settings = _store.Current;
        var slots = new List<(IPillSource Source, PillSlot Slot)>();
        foreach (var source in _sources)
        {
            IReadOnlyList<PillSlot> list;
            try { list = source.PillSlots; }
            catch (Exception ex) { CrashLog.Write(ex); continue; }
            foreach (var slot in list)
                if (settings.IsShownInPill(slot.Kind)) slots.Add((source, slot));
        }
        // Stable: within a group the modules keep their catalog order.
        slots = slots.OrderBy(s => s.Slot.Group).ToList();

        var alive = new HashSet<string>();
        var views = new List<FrameworkElement?>();
        foreach (var (_, slot) in slots)
        {
            alive.Add(slot.Id);
            if (!_views.TryGetValue(slot.Id, out var view))
            {
                try { view = slot.Create(); }
                catch (Exception ex) { CrashLog.Write(ex); view = null; }
                if (view is not null) _views[slot.Id] = view;
            }
            views.Add(view);
        }
        foreach (var id in _views.Keys.Where(id => !alive.Contains(id)).ToList()) _views.Remove(id);

        var items = new List<PillLayout.Item>();
        for (int i = 0; i < slots.Count; i++)
        {
            double width = 0;
            if (views[i] is { } v)
            {
                v.Measure(new Size(double.PositiveInfinity, PillHeight));
                width = v.DesiredSize.Width;
            }
            // A block that failed to build takes no room and is never kept.
            items.Add(new PillLayout.Item(slots[i].Slot.Group, views[i] is null ? int.MinValue : slots[i].Slot.Priority, width));
        }
        var keep = PillLayout.Fit(items, PillMaxWidth - 2 * PillPadding, PillGap, PillGroupGap);

        PillRow.Children.Clear();
        _onPill.Clear();
        PillGroup? lastGroup = null;
        for (int i = 0; i < slots.Count; i++)
        {
            if (!keep[i] || views[i] is not { } view) continue;
            if (lastGroup is { } g)
                PillRow.Children.Add(g == slots[i].Slot.Group ? new Border { Width = PillGap } : GroupDivider());
            PillRow.Children.Add(view);
            lastGroup = slots[i].Slot.Group;
            _onPill.Add(slots[i].Source);
        }

        double content = PillLayout.Width(items, keep, PillGap, PillGroupGap);
        _restWidth = PillRow.Children.Count == 0 ? PillMinWidth : Math.Clamp(content + 2 * PillPadding, PillMinWidth, PillMaxWidth);
        if (!_peeking) SetPillWidth(_restWidth);
        UpdateShown();
    }

    private static Border GroupDivider() => new()
    {
        Width = 1,
        Height = 14,
        Margin = new Thickness((PillGroupGap - 1) / 2, 0, (PillGroupGap - 1) / 2, 0),
        Background = PillLook.Faint,
        Opacity = 0.55,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Tells each module whether its blocks are seen, so figures and animations run only then.</summary>
    private void UpdateShown()
    {
        bool onScreen = IsVisible && !_expanded && _screenOn && _unlocked;
        foreach (var source in _sources)
        {
            bool shown = onScreen && _onPill.Contains(source);
            if (_shown.TryGetValue(source, out bool was) && was == shown) continue;
            _shown[source] = shown;
            try { source.SetPillShown(shown); }
            catch (Exception ex) { CrashLog.Write(ex); }
        }
    }

    // ---- A moment of attention ("Claude Code ждёт подтверждения") ----

    private void OnPeek(PillPeek peek)
    {
        if (_expanded || !IsVisible) return; // the open island shows the cards anyway
        _peekQueue.RemoveAll(p => p.Key == peek.Key);
        _peekQueue.Add(peek);
        if (!_peeking) ShowNextPeek();
    }

    private void ShowNextPeek()
    {
        _peekTimer.Stop();
        if (_peekQueue.Count == 0 || _expanded || !IsVisible) { EndPeek(); return; }
        var peek = _peekQueue[0];
        _peekQueue.RemoveAt(0);

        FrameworkElement view;
        try { view = peek.Create(); }
        catch (Exception ex) { CrashLog.Write(ex); ShowNextPeek(); return; }

        _peeking = true;
        PeekHost.Content = view;
        view.Measure(new Size(double.PositiveInfinity, PillHeight));
        SetPillWidth(Math.Clamp(view.DesiredSize.Width + 2 * PillPadding + 6, PillMinWidth, PillMaxWidth));
        Fade(PillRow, 0, delayMs: 0, durationMs: 100);
        Fade(PeekHost, 1, delayMs: 90, durationMs: 160);
        _peekTimer.Interval = peek.Duration;
        _peekTimer.Start();
    }

    private void EndPeek()
    {
        _peekTimer.Stop();
        if (!_peeking) return;
        _peeking = false;
        Fade(PeekHost, 0, delayMs: 0, durationMs: 100);
        Fade(PillRow, 1, delayMs: 90, durationMs: 160);
        SetPillWidth(_restWidth);
    }

    private void DropPeeks()
    {
        _peekQueue.Clear();
        _peekTimer.Stop();
        _peeking = false;
        PeekHost.BeginAnimation(OpacityProperty, null);
        PeekHost.Opacity = 0;
        PeekHost.Content = null;
        PillRow.BeginAnimation(OpacityProperty, null);
        PillRow.Opacity = 1;
        _collapsedRect = PillRect(_restWidth);
    }

    private static Rect PillRect(double width) => new((IslandWidth - width) / 2, 0, width, PillHeight);

    private void SetPillWidth(double width)
    {
        var target = PillRect(width);
        if (target == _collapsedRect) return;
        _collapsedRect = target;
        if (_expanded) return;
        _shape.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(_collapsedRect, TimeSpan.FromMilliseconds(280)) { EasingFunction = WidthEase });
    }

    private void OnScreenState(bool screenOn, bool unlocked)
    {
        _screenOn = screenOn;
        _unlocked = unlocked;
        UpdateShown();
    }

    /// <summary>Builds the cards that have something to show. Only while the island is open.</summary>
    private void BuildCards()
    {
        Cards.Children.Clear();
        foreach (var card in _cards)
        {
            bool has;
            try { has = card.HasContent; } catch (Exception ex) { CrashLog.Write(ex); has = false; }
            if (!has) continue;
            try
            {
                Cards.Children.Add(new Border
                {
                    Background = (Brush)FindResource("TileBrush"),
                    BorderBrush = (Brush)FindResource("DividerBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(3, 0, 3, 0),
                    Child = card.CreateCard(),
                });
            }
            catch (Exception ex) { CrashLog.Write(ex); }
        }
        EmptyHome.Visibility = Cards.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        string time = now.ToString("HH:mm");
        ClockText.Text = time;
        BigClock.Text = time;
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        string date = now.ToString("dddd, d MMMM", ru);
        DateText.Text = char.ToUpper(date[0], ru) + date[1..];
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second + 0.2);
        _clock.Start();
    }


    /// <summary>
    /// Creates the tiles. Only when the set of enabled modules changes. Their captions are filled when the
    /// island opens, so nothing asks modules for work at startup (some summaries start a background check).
    /// </summary>
    private void RebuildTiles()
    {
        _tiles = DockModules(_store.Current)
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

    // Files dragged from Explorer: the island opens, and the files go to the tile they are dropped on,
    // or to the first module that takes them. Only names are looked at while dragging.
    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        _collapseDelay.Stop();
        if (!_expanded) _expandDelay.Start();
        Island_DragOver(sender, e);
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = FileTargetFor(DroppedFiles(e), e.OriginalSource) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Island_DragLeave(object sender, DragEventArgs e)
    {
        _expandDelay.Stop();
        if (_expanded) _collapseDelay.Start();
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = DroppedFiles(e);
        var target = FileTargetFor(files, e.OriginalSource);
        _expandDelay.Stop();
        CollapseNow();
        if (target is null) return;
        try { target.OpenFiles(files.Where(target.CanOpen).ToList()); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private IFileTarget? FileTargetFor(string[] files, object? over)
    {
        if (files.Length == 0) return null;
        bool Takes(IModule? m) => m is IFileTarget t && files.Any(t.CanOpen);

        // The tile under the cursor, if it takes these files.
        if (TileUnder(over) is { } tile && _modules.Get(tile.Info.Id) is { } under && Takes(under))
            return (IFileTarget)under;

        foreach (var info in ModuleCatalog.All)
            if (_modules.Get(info.Id) is { } module && Takes(module)) return (IFileTarget)module;
        return null;
    }

    private static TileItem? TileUnder(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: TileItem tile }) return tile;
        return null;
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
        if (expand)
        {
            SetRenderMode(expanded: true);
            Height = OpenWindowHeight;
            DropPeeks();
            UpdateTiles();
            BuildCards();
            UpdateClock();
        }
        else
        {
            _clock.Stop();
        }
        UpdateShown();

        var duration = TimeSpan.FromMilliseconds(expand ? 440 : 300);
        var ease = expand ? OpenEase : CloseEase;
        double radius = expand ? ExpandedRadius : CollapsedRadius;

        var outline = new RectAnimation(expand ? ExpandedRect : _collapsedRect, duration) { EasingFunction = ease };
        _shape.BeginAnimation(RectangleGeometry.RectProperty, outline);
        _shape.BeginAnimation(RectangleGeometry.RadiusXProperty, new DoubleAnimation(radius, duration) { EasingFunction = ease });
        _shape.BeginAnimation(RectangleGeometry.RadiusYProperty, new DoubleAnimation(radius, duration) { EasingFunction = ease });

        ExpandedContent.IsHitTestVisible = expand;
        if (expand) ExpandedContent.Visibility = Visibility.Visible;
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
            DropCardsLater();
        }
    }

    /// <summary>Cards are dropped once the close animation is over, so the closed island holds no card UI.</summary>
    private void DropCardsLater()
    {
        var drop = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        drop.Tick += (_, _) =>
        {
            drop.Stop();
            if (_expanded) return;
            Cards.Children.Clear();
            Height = ClosedWindowHeight;
            ExpandedContent.Visibility = Visibility.Collapsed; // not even walked while drawing the pill
            SetRenderMode(expanded: false);
        };
        drop.Start();
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
        _clock.Stop();
        Cards.Children.Clear();
        DropPeeks();
        Height = ClosedWindowHeight;
        SetRenderMode(expanded: false);
        UpdateShown();

        _shape.BeginAnimation(RectangleGeometry.RectProperty, null);
        _shape.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
        _shape.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
        _shape.Rect = _collapsedRect;
        _shape.RadiusX = _shape.RadiusY = CollapsedRadius;

        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContent.Opacity = 0;
        ExpandedContent.IsHitTestVisible = false;
        ExpandedContent.Visibility = Visibility.Collapsed;
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
