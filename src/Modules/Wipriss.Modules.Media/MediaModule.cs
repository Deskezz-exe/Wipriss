using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Media;

/// <summary>
/// What plays right now in any program that reports to Windows (browsers with YouTube, Spotify, players):
/// the same source as the volume flyout of Windows. Windows tells us about every change by events.
/// The home screen shows up to two sources at once (say a video and music); the pill shows the first.
/// </summary>
public sealed class MediaModule : IModule, IIslandCard, IPillSource
{
    private const int MaxShown = 2;

    private Dispatcher? _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly object _gate = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _followed = [];
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _byKey = [];
    private int _generation;
    private bool _stopped, _timelineWatched, _fake;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("media");

    public Size PanelSize => new(0, 0);

    public string Summary => "";

    public HomeArea Area => HomeArea.Main;

    public event Action? Changed;

    /// <summary>The first source (the one Windows calls current, or else one that plays).</summary>
    internal NowPlaying? Current => Items.Count > 0 ? Items[0] : null;

    /// <summary>Up to two sources, the current one first.</summary>
    internal IReadOnlyList<NowPlaying> Items { get; private set; } = [];

    /// <summary>How many the home screen showed last time: going from one to two plays the split animation.</summary>
    internal int ShownCount { get; set; }

    internal static Geometry Note { get; } = MakeNote();

    public void Start(IModuleHost host)
    {
        _dispatcher = host.Dispatcher;
        // For checks of the layout without playing anything: WIPRISS_FAKE_MEDIA="Song title" (two titles: "A|B").
        if (Environment.GetEnvironmentVariable("WIPRISS_FAKE_MEDIA") is { Length: > 0 } fake)
        {
            Items = fake.Split('|').Take(MaxShown).Select((t, i) => new NowPlaying("fake" + i, t, i == 0 ? "Test Artist" : "", i == 0 ? "Spotify" : "Chrome",
                i == 0, null, true, true, i == 0 ? Color.FromRgb(0x8A, 0x2B, 0x5C) : null, TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(214), DateTime.Now)).ToList();
            _fake = true;
            UpdatePill();
            return;
        }
        _ = ConnectAsync();
    }

    public void Stop()
    {
        _stopped = true;
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }
        FollowAll([]);
        Items = [];
        _pausedTimer?.Stop();
        _pill?.Animate(false);
        _pill = null;
        _inPill = false;
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new MediaCard(this);

    // ---- The pill: what plays, and a paused song for a few minutes more ----

    private static readonly TimeSpan KeepPaused = TimeSpan.FromMinutes(3);
    private MediaPill? _pill;
    private DispatcherTimer? _pausedTimer;
    private bool _pillShown, _inPill;

    public event Action? PillChanged;

    public event Action<PillPeek>? PillPeek { add { } remove { } }

    public IReadOnlyList<PillSlot> PillSlots => _inPill && Current is not null
        ? [new PillSlot("media", "media", PillGroup.Media, 60, () => PillView())]
        : [];

    public void SetPillShown(bool shown)
    {
        _pillShown = shown;
        _pill?.Animate(shown && Current is { Playing: true });
    }

    private MediaPill PillView()
    {
        _pill ??= new MediaPill();
        if (Current is { } now) _pill.Show(now);
        _pill.Animate(_pillShown && Current is { Playing: true });
        return _pill;
    }

    /// <summary>After a change: in the pill while it plays, and a little while after a pause.</summary>
    private void UpdatePill()
    {
        bool was = _inPill;
        _pausedTimer?.Stop();
        if (Current is { Playing: true }) _inPill = true;
        else if (Current is null) _inPill = false;
        else if (_inPill && _dispatcher is not null)
        {
            _pausedTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = KeepPaused };
            _pausedTimer.Tick -= OnPausedLong;
            _pausedTimer.Tick += OnPausedLong;
            _pausedTimer.Start();
        }

        bool widthChanged = false;
        if (_inPill && _pill is not null && Current is { } now)
        {
            widthChanged = _pill.Show(now);
            _pill.Animate(_pillShown && now.Playing);
        }
        if (!_inPill) _pill = null;
        if (was != _inPill || widthChanged) PillChanged?.Invoke();
    }

    private void OnPausedLong(object? sender, EventArgs e)
    {
        _pausedTimer?.Stop();
        if (Current is { Playing: true }) return;
        _inPill = false;
        _pill = null;
        PillChanged?.Invoke();
    }

    public bool HasContent => Items.Count > 0;

    public FrameworkElement CreateCard() => new MediaCard(this);

    // ---- Buttons ----

    internal async void PlayPause(string key) => await Try(key, s => s.TryTogglePlayPauseAsync().AsTask());

    internal async void Next(string key) => await Try(key, s => s.TrySkipNextAsync().AsTask());

    internal async void Previous(string key) => await Try(key, s => s.TrySkipPreviousAsync().AsTask());

    private async Task Try(string key, Func<GlobalSystemMediaTransportControlsSession, Task> action)
    {
        GlobalSystemMediaTransportControlsSession? s;
        lock (_gate) _byKey.TryGetValue(key, out s);
        if (s is null) return;
        try { await action(s); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>
    /// The progress bar moves by itself between events; position changes (a seek) come as events, but only while
    /// the card is on screen: with the island closed nothing listens to them.
    /// </summary>
    internal void WatchTimeline(bool on)
    {
        if (on == _timelineWatched || _fake) return;
        _timelineWatched = on;
        lock (_gate)
            foreach (var s in _followed)
            {
                if (on) s.TimelinePropertiesChanged += OnTimelineChanged;
                else s.TimelinePropertiesChanged -= OnTimelineChanged;
            }
        if (on) _ = RefreshAsync();
    }

    // ---- Following the sessions ----

    private async Task ConnectAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_stopped) return;
            _manager = manager;
            manager.SessionsChanged += OnSessionsChanged;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            FollowAll(manager.GetSessions());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Older Windows or the media service is off: the card simply never shows.
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        try { FollowAll(sender.GetSessions()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _ = RefreshAsync();

    private void FollowAll(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        lock (_gate)
        {
            foreach (var old in _followed.Where(o => !sessions.Any(s => ReferenceEquals(s, o))))
            {
                old.MediaPropertiesChanged -= OnMediaChanged;
                old.PlaybackInfoChanged -= OnPlaybackChanged;
                if (_timelineWatched) old.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            foreach (var s in sessions.Where(n => !_followed.Any(o => ReferenceEquals(o, n))))
            {
                s.MediaPropertiesChanged += OnMediaChanged;
                s.PlaybackInfoChanged += OnPlaybackChanged;
                if (_timelineWatched) s.TimelinePropertiesChanged += OnTimelineChanged;
            }
            _followed.Clear();
            _followed.AddRange(sessions);
        }
        _ = RefreshAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync();

    /// <summary>Reads every source on a worker thread; the UI gets a finished snapshot of the first two.</summary>
    private async Task RefreshAsync()
    {
        int generation = Interlocked.Increment(ref _generation);
        List<GlobalSystemMediaTransportControlsSession> sessions;
        lock (_gate) sessions = [.. _followed];
        GlobalSystemMediaTransportControlsSession? current = null;
        try { current = _manager?.GetCurrentSession(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }

        var found = new List<(NowPlaying Item, GlobalSystemMediaTransportControlsSession Session, int Rank)>();
        var seen = new Dictionary<string, int>();
        foreach (var session in sessions)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props is null || string.IsNullOrWhiteSpace(props.Title)) continue;
                var playback = session.GetPlaybackInfo();
                string app = session.SourceAppUserModelId ?? "";
                int n = seen.GetValueOrDefault(app);
                seen[app] = n + 1;
                string key = app + "#" + n;
                string title = props.Title.Trim(), artist = props.Artist?.Trim() ?? "";
                var old = Items.FirstOrDefault(i => i.Key == key && i.Title == title && i.Artist == artist);
                var (cover, tint) = old is not null ? (old.Cover, old.Tint) : await LoadCoverAsync(props);

                var (position, length, at) = Timeline(session);
                bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var item = new NowPlaying(key, title, artist, AppName(app), playing, cover,
                    playback?.Controls.IsNextEnabled == true, playback?.Controls.IsPreviousEnabled == true, tint, position, length, at);
                int rank = ReferenceEquals(session, current) ? 0 : playing ? 1 : 2;
                found.Add((item, session, rank));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }

        var top = found.OrderBy(f => f.Rank).Take(MaxShown).ToList();
        if (generation != _generation || _stopped) return; // a newer refresh is on its way
        _dispatcher?.BeginInvoke(() =>
        {
            if (generation != _generation || _stopped) return;
            lock (_gate)
            {
                _byKey.Clear();
                foreach (var f in found) _byKey[f.Item.Key] = f.Session;
            }
            var next = top.Select(f => f.Item).ToList();
            if (next.SequenceEqual(Items)) return;
            Items = next;
            UpdatePill();
            Changed?.Invoke();
        });
    }

    private static (TimeSpan Position, TimeSpan Length, DateTime At) Timeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var t = session.GetTimelineProperties();
            var length = t.EndTime - t.StartTime;
            if (length <= TimeSpan.Zero || length > TimeSpan.FromHours(24)) return (TimeSpan.Zero, TimeSpan.Zero, DateTime.Now);
            var at = t.LastUpdatedTime.Year > 2000 ? t.LastUpdatedTime.LocalDateTime : DateTime.Now;
            return (t.Position - t.StartTime, length, at);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (TimeSpan.Zero, TimeSpan.Zero, DateTime.Now); }
    }

    private static async Task<(ImageSource?, Color?)> LoadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail is null) return (null, null);
        try
        {
            using var winStream = await props.Thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160; // the cover is 72 px; keep it small in memory
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return (image, CoverColor.Of(image));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, null);
        }
    }

    internal static string AppName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string s = id.ToLowerInvariant();
        if (s.Contains("chrome")) return "Chrome";
        if (s.Contains("msedge") || s.Contains("edge")) return "Edge";
        if (s.Contains("firefox")) return "Firefox";
        if (s.Contains("opera")) return "Opera";
        if (s.Contains("yandex")) return "Яндекс Браузер";
        if (s.Contains("brave")) return "Brave";
        if (s.Contains("spotify")) return "Spotify";
        if (s.Contains("zunemusic") || s.Contains("media.player")) return "Медиаплеер";
        if (s.Contains("telegram")) return "Telegram";
        string name = Path.GetFileNameWithoutExtension(id.Split('!')[0]);
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        // Installed web apps report a bare hash: say nothing rather than show it.
        if (name.Length == 0 || (name.Length >= 8 && name.All(char.IsAsciiHexDigit))) return "";
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static Geometry MakeNote()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(6.5, 18), 3.2, 2.8));
        g.Children.Add(new EllipseGeometry(new Point(17.5, 16), 3.2, 2.8));
        g.Children.Add(new RectangleGeometry(new Rect(8.4, 5.5, 1.8, 12.5)));
        g.Children.Add(new RectangleGeometry(new Rect(19.4, 3.5, 1.8, 12.5)));
        g.Children.Add(Geometry.Parse("M8.4,5.5 L21.2,2.5 L21.2,5.6 L8.4,8.6 Z"));
        g.Freeze();
        return g;
    }
}

/// <param name="Key">Which source, for the buttons.</param>
/// <param name="Tint">The cover's main colour, for the card's background.</param>
/// <param name="Position">Where it was at <paramref name="PositionAt"/>.</param>
/// <param name="Length">Zero when the program does not say.</param>
internal sealed record NowPlaying(string Key, string Title, string Artist, string App, bool Playing, ImageSource? Cover,
    bool CanNext, bool CanPrevious, Color? Tint, TimeSpan Position, TimeSpan Length, DateTime PositionAt)
{
    /// <summary>Where it is now: it moves on by itself while it plays.</summary>
    public TimeSpan PositionNow(DateTime now)
    {
        if (Length <= TimeSpan.Zero) return TimeSpan.Zero;
        var p = Playing ? Position + (now - PositionAt) : Position;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p > Length ? Length : p;
    }
}
