using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wipriss.Core.Modules;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Load;

/// <summary>
/// CPU, GPU, RAM and network speed in the closed pill. One timer, once a second, and only while the figures are
/// on screen: it stops while the island is open or hidden, the screen is off or locked, or the blocks do not fit.
/// Readings run on a worker thread; the UI only gets four short strings.
/// </summary>
public sealed class LoadModule : IModule, IPillSource, IIslandStatus
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    private readonly LoadSampler _sampler = new();
    private readonly object _gate = new();
    private Dispatcher? _dispatcher;
    private Timer? _timer;
    private Metric? _cpu, _gpu, _ram;
    private NetMetric? _net;
    private bool _shown, _reading;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("load");

    public Size PanelSize => new(0, 0);

    public string Summary => "";

    public event Action? PillChanged;

    public event Action<PillPeek>? PillPeek { add { } remove { } }

    public void Start(IModuleHost host)
    {
        _dispatcher = host.Dispatcher;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _sampler.Dispose();
        }
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new TextBlock { Text = "Цифры показываются в пилюле вверху экрана." };

    public IReadOnlyList<PillSlot> PillSlots
    {
        get
        {
            var slots = new List<PillSlot>
            {
                new("load.cpu", "load.cpu", PillGroup.Load, 50, () => _cpu ??= new Metric("CPU")),
            };
            // No GPU counters on this PC (old Windows, a virtual machine): the block does not take room.
            if (_sampler.HasGpu) slots.Add(new("load.gpu", "load.gpu", PillGroup.Load, 35, () => _gpu ??= new Metric("GPU")));
            slots.Add(new("load.ram", "load.ram", PillGroup.Load, 45, () => _ram ??= new Metric("RAM")));
            slots.Add(new("load.net", "load.net", PillGroup.Load, 30, () => _net ??= new NetMetric()));
            return slots;
        }
    }

    public void SetPillShown(bool shown)
    {
        _pillShown = shown;
        SetActive(_pillShown || _statusShown);
    }

    // ---- The open island: the same figures in its top row, while it is open ----

    private bool _pillShown, _statusShown;
    private StatusRow? _status;

    public FrameworkElement CreateStatus()
    {
        var row = new StatusRow(_sampler.HasGpu);
        row.Loaded += (_, _) => { _status = row; _statusShown = true; SetActive(true); };
        row.Unloaded += (_, _) =>
        {
            if (_status == row) _status = null;
            _statusShown = false;
            SetActive(_pillShown);
        };
        return row;
    }

    private void SetActive(bool shown)
    {
        lock (_gate)
        {
            if (shown == _shown) return;
            _shown = shown;
            if (_timer is null) return;
            if (shown)
            {
                _sampler.Reset();
                _timer.Change(TimeSpan.Zero, Period); // the first reading only sets the start
            }
            else
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                // Counters hold some memory; while the figures are not seen they are closed.
                if (!_reading) _sampler.Dispose();
            }
        }
    }

    private void Tick()
    {
        LoadReading r;
        bool hadGpu;
        lock (_gate)
        {
            if (!_shown || _timer is null || _reading) return;
            _reading = true;
            hadGpu = _sampler.HasGpu;
        }
        try { r = _sampler.Read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { r = new LoadReading(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN); }
        bool gpuGone;
        lock (_gate)
        {
            _reading = false;
            gpuGone = hadGpu && !_sampler.HasGpu;
            if (!_shown) _sampler.Dispose();
        }

        _dispatcher?.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _cpu?.Show(r.Cpu, high: 90);
            _gpu?.Show(r.Gpu, high: 90);
            _ram?.Show(r.Ram, high: 90);
            _net?.Show(r.Down, r.Up);
            _status?.Show(r);
            if (gpuGone) { _gpu = null; PillChanged?.Invoke(); }
        });
    }

    /// <summary>"CPU 12%": a quiet label and a figure of fixed width, so the pill never shifts.</summary>
    private sealed class Metric : StackPanel
    {
        private static readonly double ValueWidth = PillLook.TextWidth("100%");
        private readonly TextBlock _value;
        private string _text = "";

        public Metric(string label)
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            var name = PillLook.Label(label, PillLook.Subtle, 10.5);
            name.FontFamily = PillLook.SmallFont;
            name.Margin = new Thickness(0, 1, 5, 0);
            Children.Add(name);
            _value = PillLook.Label("–");
            _value.Width = ValueWidth;
            _value.TextAlignment = TextAlignment.Left;
            Children.Add(_value);
        }

        public void Show(double value, double high)
        {
            // Right after a pause there is no fresh figure yet: the last one stays meanwhile.
            if (double.IsNaN(value) && _text.Length > 0) return;
            string text = double.IsNaN(value) ? "–" : LoadMath.Percent(value);
            if (text == _text) return; // nothing to redraw
            _text = text;
            _value.Text = text;
            _value.Foreground = !double.IsNaN(value) && value >= high ? PillLook.Red : PillLook.Text;
        }
    }

    /// <summary>
    /// The open island's top row: CPU · GPU · RAM with a thin bar under each, then the network speed.
    /// Fixed widths, so the row never shifts as the figures change.
    /// </summary>
    private sealed class StatusRow : StackPanel
    {
        private readonly List<(TextBlock Value, System.Windows.Media.ScaleTransform Bar, Func<LoadReading, double> Pick)> _metrics = [];
        private readonly TextBlock _down, _up;

        public StatusRow(bool gpu)
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            Add("CPU", r => r.Cpu);
            if (gpu) Add("GPU", r => r.Gpu);
            Add("RAM", r => r.Ram);

            var net = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            var netName = PillLook.Label("Сеть", PillLook.Subtle, 10.5);
            netName.FontFamily = PillLook.SmallFont;
            netName.Margin = new Thickness(0, 1, 6, 0);
            net.Children.Add(netName);
            net.Children.Add(PillLook.IconView(PillLook.ArrowDown, PillLook.Subtle, 11));
            _down = Value(Math.Max(PillLook.TextWidth("888K"), PillLook.TextWidth("888M")));
            _down.Margin = new Thickness(2, 0, 6, 0);
            net.Children.Add(_down);
            net.Children.Add(PillLook.IconView(PillLook.ArrowUp, PillLook.Subtle, 11));
            _up = Value(_down.Width);
            _up.Margin = new Thickness(2, 0, 0, 0);
            net.Children.Add(_up);
            Children.Add(net);
        }

        private static TextBlock Value(double width)
        {
            var t = PillLook.Label("–");
            t.Width = width;
            t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        private void Add(string label, Func<LoadReading, double> pick)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 13, 0), VerticalAlignment = VerticalAlignment.Center };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var name = PillLook.Label(label, PillLook.Subtle, 10.5);
            name.FontFamily = PillLook.SmallFont;
            name.Margin = new Thickness(0, 1, 5, 0);
            line.Children.Add(name);
            var value = Value(PillLook.TextWidth("100%"));
            line.Children.Add(value);
            box.Children.Add(line);
            var bar = HomeLook.Bar(3, PillLook.Text, out var scale);
            bar.Margin = new Thickness(0, 3, 0, 0);
            box.Children.Add(bar);
            Children.Add(box);
            _metrics.Add((value, scale, pick));
        }

        public void Show(LoadReading r)
        {
            foreach (var (value, bar, pick) in _metrics)
            {
                double v = pick(r);
                if (double.IsNaN(v)) continue;
                value.Text = LoadMath.Percent(v);
                value.Foreground = v >= 90 ? PillLook.Red : PillLook.Text;
                bar.ScaleX = Math.Clamp(v / 100, 0, 1);
            }
            if (!double.IsNaN(r.Down)) _down.Text = LoadMath.Speed(r.Down);
            if (!double.IsNaN(r.Up)) _up.Text = LoadMath.Speed(r.Up);
        }
    }

    /// <summary>"↓ 1.2M ↑ 85K": download and upload per second.</summary>
    private sealed class NetMetric : StackPanel
    {
        private static readonly double ValueWidth = Math.Max(PillLook.TextWidth("888K"), PillLook.TextWidth("888M"));
        private readonly TextBlock _down, _up;
        private string _text = "";

        public NetMetric()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            var name = PillLook.Label("Сеть", PillLook.Subtle, 10.5);
            name.FontFamily = PillLook.SmallFont;
            name.Margin = new Thickness(0, 1, 5, 0);
            Children.Add(name);
            Children.Add(PillLook.IconView(PillLook.ArrowDown, PillLook.Subtle, 11));
            _down = Value(new Thickness(2, 0, 6, 0));
            Children.Add(_down);
            Children.Add(PillLook.IconView(PillLook.ArrowUp, PillLook.Subtle, 11));
            _up = Value(new Thickness(2, 0, 0, 0));
            Children.Add(_up);
        }

        private TextBlock Value(Thickness margin)
        {
            var t = PillLook.Label("–");
            t.Width = ValueWidth;
            t.Margin = margin;
            return t;
        }

        public void Show(double down, double up)
        {
            if ((double.IsNaN(down) || double.IsNaN(up)) && _text.Length > 0) return;
            string d = double.IsNaN(down) ? "–" : LoadMath.Speed(down);
            string u = double.IsNaN(up) ? "–" : LoadMath.Speed(up);
            if (d + u == _text) return;
            _text = d + u;
            _down.Text = d;
            _up.Text = u;
        }
    }
}
