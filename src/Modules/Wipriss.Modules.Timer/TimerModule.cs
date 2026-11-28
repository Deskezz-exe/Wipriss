using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Wipriss.Core.Modules;
using Wipriss.Core.Timing;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Timer;

/// <summary>
/// One simple timer: 5, 15 or 25 minutes, start, pause, reset. While it runs nothing ticks: a Windows waitable
/// timer wakes Wipriss once, at the end (then a Windows sound and a moment in the pill). The card counts seconds
/// only while the island is open; the pill block changes once a minute and only while it is seen.
/// </summary>
public sealed class TimerModule : IModule, IIslandCard, IPillSource
{
    private static readonly int[] Presets = [5, 15, 25];

    private readonly TimerClock _clock = new();
    private AbsoluteTimer? _end;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _pillTick;
    private TimerPill? _pill;
    private bool _pillShown, _hadSlot;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("timer");

    public Size PanelSize => new(0, 0);

    public string Summary => "";

    public HomeArea Area => HomeArea.SideTop;

    public bool HasContent => true;

    public event Action? Changed;

    public event Action? PillChanged;

    public event Action<PillPeek>? PillPeek;

    public void Start(IModuleHost host)
    {
        _dispatcher = host.Dispatcher;
        _end = new AbsoluteTimer(() => _dispatcher?.BeginInvoke(OnEnd));
        _pillTick = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _pillTick.Tick += (_, _) => UpdatePill();
    }

    public void Stop()
    {
        _end?.Dispose();
        _end = null;
        _pillTick?.Stop();
        _clock.Reset();
        _pill = null;
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new TimerCard(this);

    public FrameworkElement CreateCard() => new TimerCard(this);

    // ---- Actions (UI thread) ----

    internal TimerClock Clock => _clock;

    internal IReadOnlyList<int> PresetMinutes => Presets;

    internal void Preset(int minutes)
    {
        _clock.Preset(TimeSpan.FromMinutes(minutes), DateTime.Now);
        Scheduled();
    }

    internal void Toggle()
    {
        if (_clock.Running) _clock.Pause(DateTime.Now);
        else _clock.Start(DateTime.Now);
        Scheduled();
    }

    internal void Reset()
    {
        _clock.Reset();
        Scheduled();
    }

    private void Scheduled()
    {
        try
        {
            if (_clock.EndsAt is { } end) _end?.Schedule(end.ToUniversalTime());
            else _end?.Cancel();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        Raise();
    }

    private void OnEnd()
    {
        // The waitable timer can fire a hair early or after a pause that was too late to cancel it.
        if (_clock.EndsAt is not { } end || end - DateTime.Now > TimeSpan.FromMilliseconds(500)) return;
        _clock.Finish();
        MessageBeep(MB_ICONASTERISK);
        PillPeek?.Invoke(new PillPeek("timer.end", () => TimerPill.Peek(), TimeSpan.FromSeconds(5)));
        Raise();
    }

    private void Raise()
    {
        bool slot = _clock.Running || _clock.Paused;
        if (!slot) _pill = null;
        UpdatePill();
        if (slot != _hadSlot) { _hadSlot = slot; PillChanged?.Invoke(); }
        Changed?.Invoke();
    }

    // ---- The pill: the minutes left, while it runs or is paused ----

    public IReadOnlyList<PillSlot> PillSlots => _clock.Running || _clock.Paused
        ? [new PillSlot("timer", "timer", PillGroup.Media, 58, () => _pill ??= NewPill())]
        : [];

    private TimerPill NewPill()
    {
        var pill = new TimerPill();
        pill.Show(_clock.Left(DateTime.Now), _clock.Running);
        return pill;
    }

    public void SetPillShown(bool shown)
    {
        _pillShown = shown;
        UpdatePill();
    }

    /// <summary>Shows the minutes and wakes up once, when the figure changes next.</summary>
    private void UpdatePill()
    {
        _pillTick?.Stop();
        var left = _clock.Left(DateTime.Now);
        _pill?.Show(left, _clock.Running);
        if (!_pillShown || !_clock.Running || _pillTick is null || left <= TimeSpan.Zero) return;
        var minutes = TimeSpan.FromMinutes(Math.Ceiling(left.TotalMinutes - 0.001) - 1);
        var wait = left - minutes;
        _pillTick.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait + TimeSpan.FromMilliseconds(50);
        _pillTick.Start();
    }

    private const uint MB_ICONASTERISK = 0x40;

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);
}

/// <summary>The pill block: a stopwatch and "12 мин".</summary>
internal sealed class TimerPill : StackPanel
{
    private readonly TextBlock _text;

    public TimerPill()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        var icon = HomeLook.Glyph("", 12.5, PillLook.Amber);
        icon.Margin = new Thickness(0, 1, 6, 0);
        Children.Add(icon);
        _text = PillLook.Label("");
        _text.Width = PillLook.TextWidth("888 мин");
        Children.Add(_text);
    }

    public void Show(TimeSpan left, bool running)
    {
        _text.Text = TimerClock.Short(left);
        _text.Foreground = running ? PillLook.Text : PillLook.Subtle;
    }

    public static FrameworkElement Peek()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = HomeLook.Glyph("", 13, PillLook.Amber);
        icon.Margin = new Thickness(0, 1, 8, 0);
        row.Children.Add(icon);
        row.Children.Add(PillLook.Label("Таймер: время вышло"));
        return row;
    }
}

/// <summary>
/// The home-screen card: the time left in big figures, presets 5 / 15 / 25, reset and start/pause.
/// It counts seconds only while it is on screen.
/// </summary>
internal sealed class TimerCard : Border
{
    private readonly TimerModule _module;
    private readonly TextBlock _time, _note;
    private readonly TextBlock _playGlyph;
    private readonly Border _play;
    private readonly List<(int Minutes, Border Chip, TextBlock Text)> _chips = [];
    private readonly DispatcherTimer _tick;

    public TimerCard(TimerModule module)
    {
        _module = module;
        Background = HomeLook.CardFill;
        BorderBrush = HomeLook.CardEdge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(HomeLook.CardRadius);
        Padding = new Thickness(16, 12, 12, 12);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Child = grid;

        // Top: the icon, the name and what it does now; the figures on the right.
        var top = new DockPanel();
        _time = HomeLook.Text("25:00", 26, PillLook.Text, bold: true);
        _time.FontFamily = HomeLook.DisplayFont;
        Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        _time.Margin = new Thickness(0, -4, 4, 0);
        DockPanel.SetDock(_time, Dock.Right);
        top.Children.Add(_time);

        var icon = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = HomeLook.Raised,
            Child = HomeLook.Glyph("", 14, PillLook.Amber), Margin = new Thickness(0, 0, 10, 0),
        };
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(HomeLook.Text("Таймер", 13.5, bold: true));
        _note = HomeLook.Text("", 11.5, PillLook.Subtle);
        names.Children.Add(_note);
        top.Children.Add(names);
        grid.Children.Add(top);

        // Bottom: presets on the left, reset and start/pause on the right.
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(bottom, 1);
        _playGlyph = HomeLook.Glyph("", 13, Brushes.Black);
        _play = HomeLook.Button(_playGlyph, 34, 34, () => { _module.Toggle(); Refresh(); }, "Старт / пауза",
            fill: PillLook.Text, hover: new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        DockPanel.SetDock(_play, Dock.Right);
        bottom.Children.Add(_play);
        var reset = HomeLook.Button(HomeLook.Glyph("", 13, PillLook.Subtle), 34, 34, () => { _module.Reset(); Refresh(); }, "Сбросить",
            fill: HomeLook.Raised);
        reset.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Add(reset);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (int m in module.PresetMinutes)
        {
            var text = HomeLook.Text($"{m} мин", 12, PillLook.Subtle);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            int minutes = m;
            var chip = HomeLook.Button(text, 58, 30, () => { _module.Preset(minutes); Refresh(); }, null, fill: HomeLook.Raised, radius: 15);
            chip.Margin = new Thickness(0, 0, 6, 0);
            chips.Children.Add(chip);
            _chips.Add((m, chip, text));
        }
        bottom.Children.Add(chips);
        grid.Children.Add(bottom);

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _module.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => { _module.Changed -= Refresh; _tick.Stop(); };
    }

    private void Refresh()
    {
        var clock = _module.Clock;
        var now = DateTime.Now;
        var left = clock.Left(now);
        _time.Text = TimerClock.Format(left);
        _time.Foreground = clock.Running ? PillLook.Text : clock.Paused ? PillLook.Amber : PillLook.Subtle;
        _note.Text = clock.Running ? $"идёт · до {clock.EndsAt:HH:mm}"
            : clock.Paused ? "на паузе"
            : $"{(int)clock.Length.TotalMinutes} минут";
        _playGlyph.Text = clock.Running ? "" : "";
        foreach (var (minutes, chip, text) in _chips)
        {
            bool on = (int)clock.Length.TotalMinutes == minutes;
            text.Foreground = on ? PillLook.Text : PillLook.Subtle;
            chip.BorderBrush = on ? PillLook.Faint : null;
            chip.BorderThickness = new Thickness(on ? 1 : 0);
        }
        if (clock.Running && IsLoaded) { if (!_tick.IsEnabled) _tick.Start(); }
        else _tick.Stop();
    }
}
