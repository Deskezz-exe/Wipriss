using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wipriss.Core.Modules;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Batteries;

/// <summary>
/// The charge of connected mice, keyboards and headphones in the closed pill: Bluetooth ones (Windows keeps their
/// charge) and wireless receivers (asked by their own protocol). A device that connects or disconnects is noticed
/// by events; the charge is read then and every few minutes while the pill is seen.
/// For checks without the hardware: WIPRISS_FAKE_BATTERIES="mouse|Name|5|0;keyboard|K|100|1;headset|Наушники|60|0".
/// </summary>
public sealed class BatteriesModule : IModule, IPillSource, IIslandStatus
{
    // ---- The open island: the devices next to the settings button ----

    private StackPanel? _status;

    public bool StatusOnRight => true;

    public FrameworkElement CreateStatus()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Loaded += (_, _) => { _status = row; FillStatus(); };
        row.Unloaded += (_, _) => { if (_status == row) _status = null; };
        return row;
    }

    /// <summary>One small device each: its icon, battery and percent; the name in the tooltip.</summary>
    private void FillStatus()
    {
        if (_status is null) return;
        _status.Children.Clear();
        foreach (var d in _devices)
        {
            var view = new BatteryPill { ToolTip = d.Name, Margin = new Thickness(_status.Children.Count == 0 ? 0 : 12, 0, 0, 0) };
            view.Show(d);
            _status.Children.Add(view);
        }
    }

    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly BluetoothBatteries _bluetooth = new();
    private readonly Dictionary<string, BatteryPill> _views = [];
    private List<BatteryDevice> _devices = [];
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _refresh, _soon;
    private Windows.Devices.Enumeration.DeviceWatcher? _hidWatcher;
    private bool _shown, _reading, _again, _stopped;
    private DateTime _lastRead = DateTime.MinValue;
    private readonly List<BatteryDevice>? _fake = Fake(Environment.GetEnvironmentVariable("WIPRISS_FAKE_BATTERIES"));

    public ModuleInfo Info { get; } = ModuleCatalog.Get("batteries");

    public Size PanelSize => new(0, 0);

    public string Summary => "";

    public event Action? PillChanged;

    public event Action<PillPeek>? PillPeek { add { } remove { } }

    public void Start(IModuleHost host)
    {
        _dispatcher = host.Dispatcher;
        _refresh = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = Refresh };
        _refresh.Tick += (_, _) => ReadSoon(TimeSpan.Zero);
        _soon = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _soon.Tick += (_, _) => { _soon.Stop(); ReadNow(); };

        if (_fake is not null)
        {
            _devices = _fake;
            return;
        }
        _bluetooth.Changed += () => _dispatcher.BeginInvoke(() => { _retry = 0; ReadSoon(Debounce); });
        _bluetooth.Start();
        WatchHid();
        ReadSoon(Debounce);
    }

    public void Stop()
    {
        _stopped = true;
        _refresh?.Stop();
        _soon?.Stop();
        _bluetooth.Dispose();
        try { if (_hidWatcher is { Status: Windows.Devices.Enumeration.DeviceWatcherStatus.Started or Windows.Devices.Enumeration.DeviceWatcherStatus.EnumerationCompleted }) _hidWatcher.Stop(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _hidWatcher = null;
        _views.Clear();
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new TextBlock { Text = "Заряд показывается в пилюле вверху экрана." };

    public IReadOnlyList<PillSlot> PillSlots => _devices
        .Select((d, i) => new PillSlot("battery." + d.Key, "battery", PillGroup.Battery, (d.Percent < 20 ? 65 : 55) - i, () => View(d.Key)))
        .ToList();

    public void SetPillShown(bool shown)
    {
        if (shown == _shown) return;
        _shown = shown;
        if (_fake is not null || _refresh is null) return;
        if (shown)
        {
            _refresh.Start();
            if (DateTime.Now - _lastRead > Refresh) ReadSoon(TimeSpan.Zero);
        }
        else _refresh.Stop();
    }

    private BatteryPill View(string key)
    {
        if (!_views.TryGetValue(key, out var view)) _views[key] = view = new BatteryPill();
        if (_devices.FirstOrDefault(d => d.Key == key) is { } device) view.Show(device);
        return view;
    }

    /// <summary>New and removed USB devices (a receiver plugged in) are events too.</summary>
    private void WatchHid()
    {
        try
        {
            string aqs = "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";
            var watcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(aqs);
            bool ready = false;
            watcher.EnumerationCompleted += (_, _) => ready = true;
            watcher.Added += (_, _) => { if (ready) _dispatcher?.BeginInvoke(() => ReadSoon(Debounce)); };
            watcher.Removed += (_, _) => { if (ready) _dispatcher?.BeginInvoke(() => ReadSoon(Debounce)); };
            watcher.Updated += (_, _) => { };
            watcher.Start();
            _hidWatcher = watcher;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _hidWatcher = null; }
    }

    private void ReadSoon(TimeSpan delay)
    {
        if (_stopped || _soon is null) return;
        _soon.Stop();
        _soon.Interval = delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay;
        _soon.Start();
    }

    /// <summary>Reads everything on a worker thread; one reading at a time.</summary>
    private void ReadNow()
    {
        if (_reading) { _again = true; return; }
        _reading = true;
        _lastRead = DateTime.Now;
        Task.Run(() =>
        {
            var found = new List<BatteryDevice>();
            try { found.AddRange(_bluetooth.Read()); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            try { found.AddRange(HidBatteries.Read()); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            return found;
        }).ContinueWith(t =>
        {
            _dispatcher?.BeginInvoke(() =>
            {
                _reading = false;
                if (_stopped) return;
                Apply(t.IsCompletedSuccessfully ? t.Result : []);
                if (_again) { _again = false; ReadSoon(TimeSpan.Zero); }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Headphones tell Windows their charge some seconds after they connect (and some never do). While a Bluetooth
    /// device is connected but has no figure yet, it is asked again a few times, then left until the next event.
    /// </summary>
    private static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2)];
    private int _retry;

    private void Apply(List<BatteryDevice> found)
    {
        int bluetooth = found.Count(d => d.Key.StartsWith("bt-", StringComparison.Ordinal));
        if (_bluetooth.ConnectedCount > bluetooth && _retry < Retries.Length) ReadSoon(Retries[_retry++]);

        found = found.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToList();
        bool sameSet = found.Select(d => d.Key).SequenceEqual(_devices.Select(d => d.Key));
        _devices = found;
        foreach (var key in _views.Keys.Where(k => found.All(d => d.Key != k)).ToList()) _views.Remove(key);
        foreach (var d in found)
            if (_views.TryGetValue(d.Key, out var view)) view.Show(d);
        if (!sameSet) PillChanged?.Invoke();
        FillStatus();
    }

    internal static List<BatteryDevice>? Fake(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var list = new List<BatteryDevice>();
        int n = 0;
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = part.Split('|');
            if (f.Length < 3 || !int.TryParse(f[2], out int pct)) continue;
            var kind = f[0].ToLowerInvariant() switch
            {
                "mouse" => DeviceKind.Mouse,
                "keyboard" => DeviceKind.Keyboard,
                "headset" => DeviceKind.Headset,
                _ => DeviceKind.Other,
            };
            list.Add(new BatteryDevice($"fake{n++}", f[1], kind, Math.Clamp(pct, 0, 100), f.Length > 3 && f[3] == "1"));
        }
        return list;
    }
}

/// <summary>One device in the pill: what it is, a small battery that fills with its charge, and the figure.</summary>
internal sealed class BatteryPill : StackPanel
{
    private static readonly double PercentWidth = PillLook.TextWidth("100%");
    private readonly ContentControl _icon = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Rectangle _fill;
    private readonly TextBlock _percent;
    private BatteryDevice? _shown;

    public BatteryPill()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_icon);

        var battery = new Canvas { Width = 18, Height = 10, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        battery.Children.Add(new Rectangle { Width = 16, Height = 10, RadiusX = 2.5, RadiusY = 2.5, Stroke = PillLook.Subtle, StrokeThickness = 1 });
        var cap = new Rectangle { Width = 1.5, Height = 4, RadiusX = 0.7, RadiusY = 0.7, Fill = PillLook.Subtle };
        Canvas.SetLeft(cap, 16.5);
        Canvas.SetTop(cap, 3);
        battery.Children.Add(cap);
        _fill = new Rectangle { Height = 6, RadiusX = 1.2, RadiusY = 1.2 };
        Canvas.SetLeft(_fill, 2);
        Canvas.SetTop(_fill, 2);
        battery.Children.Add(_fill);
        Children.Add(battery);

        _percent = PillLook.Label("");
        _percent.Width = PercentWidth;
        Children.Add(_percent);
    }

    public void Show(BatteryDevice d)
    {
        if (_shown == d) return;
        if (_shown?.Kind != d.Kind)
            _icon.Content = PillLook.IconView(d.Kind switch
            {
                DeviceKind.Mouse => PillLook.Mouse,
                DeviceKind.Keyboard => PillLook.Keyboard,
                DeviceKind.Headset => PillLook.Headphones,
                _ => PillLook.Headset,
            }, PillLook.Text, 13);
        _shown = d;
        var brush = d.Charging ? PillLook.Green : d.Percent < 20 ? PillLook.Red : PillLook.Text;
        _fill.Width = Math.Max(1, 12 * d.Percent / 100.0);
        _fill.Fill = brush;
        _percent.Text = d.Percent + "%";
        _percent.Foreground = d.Percent < 20 && !d.Charging ? PillLook.Red : PillLook.Text;
    }
}
