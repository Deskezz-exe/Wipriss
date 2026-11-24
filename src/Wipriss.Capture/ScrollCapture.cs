namespace Wipriss.Capture;

/// <summary>
/// Scrolls an area with the mouse wheel and glues the frames together. Stops at the end of the page,
/// when the mouse is moved, when asked to (token), or at <see cref="MaxHeight"/>. The cursor is put back where it was.
/// Windows running as administrator can be captured but not scrolled: Windows blocks wheel input to them.
/// </summary>
public static class ScrollCapture
{
    public const int MaxHeight = 30_000;
    private const int MaxSteps = 400;

    public static async Task<ScreenImage> RunAsync(PixelRect area, IProgress<int>? progress, CancellationToken token)
    {
        var (cursorX, cursorY) = ScreenGrabber.CursorPosition();
        int centerX = area.X + area.Width / 2, centerY = area.Y + area.Height / 2;
        var stitcher = new ScrollStitcher(area.Width, area.Height);
        bool userTookCursor = false;
        try
        {
            stitcher.Add(await StableFrame(area, token).ConfigureAwait(false));
            progress?.Report(stitcher.Height);

            int notches = area.Height < 400 ? 1 : 3, stillCount = 0;
            for (int step = 0; step < MaxSteps && stitcher.Height < MaxHeight && !token.IsCancellationRequested; step++)
            {
                // Moving the mouse means "stop": the person takes the cursor back.
                if (step > 0)
                {
                    var (x, y) = ScreenGrabber.CursorPosition();
                    if (Math.Abs(x - centerX) > 4 || Math.Abs(y - centerY) > 4)
                    {
                        userTookCursor = true;
                        break;
                    }
                }

                Desktop.MoveCursor(centerX, centerY);
                Desktop.Wheel(-120 * notches);
                var frame = await StableFrame(area, token).ConfigureAwait(false);

                switch (stitcher.Add(frame))
                {
                    case StitchStep.Added:
                        stillCount = 0;
                        // Aim for a bit over half the area per step: big steps, safe overlap.
                        double perNotch = Math.Max(1.0, (double)stitcher.LastShift / notches);
                        notches = Math.Clamp((int)(area.Height * 0.55 / perNotch), 1, 15);
                        progress?.Report(stitcher.Height);
                        break;
                    case StitchStep.NoChange:
                        if (++stillCount >= 2) return stitcher.Build(); // the end of the page
                        break;
                    default:
                        return stitcher.Build(); // can't continue safely: keep what is glued so far
                }
            }

            return stitcher.Build();
        }
        catch (OperationCanceledException)
        {
            return stitcher.Build();
        }
        finally
        {
            if (!userTookCursor) Desktop.MoveCursor(cursorX, cursorY);
        }
    }

    /// <summary>Waits until smooth scrolling and repainting settle: two identical frames in a row (or ~0.7 s).</summary>
    private static async Task<ScreenImage> StableFrame(PixelRect area, CancellationToken token)
    {
        var frame = ScreenGrabber.Grab(area);
        for (int i = 0; i < 14; i++)
        {
            await Task.Delay(50, token).ConfigureAwait(false);
            var next = ScreenGrabber.Grab(area);
            if (next.SameAs(frame)) return next;
            frame = next;
        }

        return frame;
    }
}
