using System.Windows;
using Wipriss.Capture;
using Wipriss.Core;

namespace Wipriss.Modules.Capture;

internal enum CaptureMode { Area, Scroll }

/// <summary>
/// One screenshot from PrtSc to the clipboard. The screen is copied first, before any Wipriss window
/// appears, so open menus and tooltips stay in the picture. Then each monitor gets an overlay showing
/// that frozen copy, where you pick what to keep.
/// </summary>
internal sealed class CaptureSession(CaptureModule module)
{
    private readonly List<OverlayWindow> _overlays = [];
    private CaptureMode _mode = CaptureMode.Area;
    private bool _finished;

    public CaptureMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            foreach (var overlay in _overlays) overlay.ModeChanged();
        }
    }

    public void Start()
    {
        var monitors = ScreenGrabber.Monitors();
        var frozen = monitors.Select(m => (Monitor: m, Image: ScreenGrabber.Grab(m.Bounds))).ToList();
        var windows = Desktop.VisibleWindows();
        var (x, y) = ScreenGrabber.CursorPosition();
        var active = monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? monitors.First(m => m.IsPrimary);

        foreach (var (monitor, image) in frozen)
        {
            var overlay = new OverlayWindow(this, monitor, image, windows, withToolbar: monitor == active);
            _overlays.Add(overlay);
            overlay.Show();
        }

        _overlays.First(o => o.HasToolbar).Focus();
    }

    public void Cancel() => End();

    /// <summary>A rectangle of one monitor's frozen picture (pixels relative to that monitor).</summary>
    public void TakeArea(OverlayWindow from, PixelRect area)
    {
        var image = from.Frozen.Crop(area);
        End();
        Deliver(image);
    }

    public void TakeScreen(OverlayWindow from)
    {
        var image = from.Frozen;
        End();
        Deliver(image);
    }

    /// <summary>Scrolls a live area of the screen (desktop pixels) and glues it into one tall picture.</summary>
    public async void TakeScroll(PixelRect area)
    {
        CloseOverlays();
        var status = new ScrollStatusWindow(area);
        var stop = new CancellationTokenSource();
        status.Show();
        module.WatchStopKey(stop.Cancel);
        try
        {
            await Task.Delay(150); // let the overlays disappear from the screen before the first frame
            var progress = new Progress<int>(status.ShowHeight);
            var image = await Task.Run(() => ScrollCapture.RunAsync(area, progress, stop.Token));
            status.Close();
            End();
            Deliver(image);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            status.Close();
            End();
        }
        finally
        {
            module.WatchStopKey(null);
            stop.Dispose();
        }
    }

    private void Deliver(ScreenImage image)
    {
        var bitmap = Images.ToBitmap(image);
        bool onClipboard = ClipboardWriter.Put(bitmap);
        module.Deliver(bitmap, onClipboard);
    }

    private void End()
    {
        CloseOverlays();
        if (_finished) return;
        _finished = true;
        module.SessionEnded();
    }

    private void CloseOverlays()
    {
        foreach (var overlay in _overlays.ToList()) overlay.Close();
        _overlays.Clear();
    }
}
