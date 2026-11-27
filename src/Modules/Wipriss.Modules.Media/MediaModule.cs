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
/// </summary>
public sealed class MediaModule : IModule, IIslandCard
{
    private Dispatcher? _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _generation;
    private bool _stopped;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("media");

    public Size PanelSize => new(0, 0);

    public string Summary => "";

    public event Action? Changed;

    internal NowPlaying? Current { get; private set; }

    internal static Geometry Note { get; } = MakeNote();

    public void Start(IModuleHost host)
    {
        _dispatcher = host.Dispatcher;
        _ = ConnectAsync();
    }

    public void Stop()
    {
        _stopped = true;
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }
        Follow(null);
        Current = null;
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new MediaCard(this);

    public IslandActivity? Activity =>
        Current is { Playing: true } now ? new IslandActivity(now.Title, ActivityState.Info, Note, null) : null;

    public bool HasContent => Current is not null;

    public FrameworkElement CreateCard() => new MediaCard(this);

    internal async void PlayPause() => await Try(s => s.TryTogglePlayPauseAsync().AsTask());

    internal async void Next() => await Try(s => s.TrySkipNextAsync().AsTask());

    internal async void Previous() => await Try(s => s.TrySkipPreviousAsync().AsTask());

    private async Task Try(Func<GlobalSystemMediaTransportControlsSession, Task> action)
    {
        if (_session is not { } s) return;
        try { await action(s); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private async Task ConnectAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_stopped) return;
            _manager = manager;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            Follow(manager.GetCurrentSession());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Older Windows or the media service is off: the card simply never shows.
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Follow(sender.GetCurrentSession());

    private void Follow(GlobalSystemMediaTransportControlsSession? session)
    {
        var old = Interlocked.Exchange(ref _session, session);
        if (old is not null && !ReferenceEquals(old, session))
        {
            old.MediaPropertiesChanged -= OnMediaChanged;
            old.PlaybackInfoChanged -= OnPlaybackChanged;
        }
        if (session is not null && !ReferenceEquals(old, session))
        {
            session.MediaPropertiesChanged += OnMediaChanged;
            session.PlaybackInfoChanged += OnPlaybackChanged;
        }
        _ = RefreshAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();

    /// <summary>Reads title, artist, state and a small cover. Runs on a worker thread; the UI gets a finished snapshot.</summary>
    private async Task RefreshAsync()
    {
        int generation = Interlocked.Increment(ref _generation);
        NowPlaying? next = null;
        var session = _session;
        if (session is not null)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                if (props is not null && !string.IsNullOrWhiteSpace(props.Title))
                {
                    var cover = Current is { } prev && prev.Title == props.Title && prev.Artist == props.Artist
                        ? prev.Cover
                        : await LoadCoverAsync(props);
                    next = new NowPlaying(
                        props.Title.Trim(),
                        props.Artist?.Trim() ?? "",
                        AppName(session.SourceAppUserModelId),
                        playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        cover,
                        playback?.Controls.IsNextEnabled == true,
                        playback?.Controls.IsPreviousEnabled == true);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                next = null;
            }
        }

        if (generation != _generation || _stopped) return; // a newer refresh is on its way
        _dispatcher?.BeginInvoke(() =>
        {
            if (generation != _generation || _stopped) return;
            if (next == Current) return;
            Current = next;
            Changed?.Invoke();
        });
    }

    private static async Task<ImageSource?> LoadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail is null) return null;
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
            image.DecodePixelWidth = 112; // the cover is 56 px; keep it small in memory
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
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

internal sealed record NowPlaying(string Title, string Artist, string App, bool Playing, ImageSource? Cover, bool CanNext, bool CanPrevious);
