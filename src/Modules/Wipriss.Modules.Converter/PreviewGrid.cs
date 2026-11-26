using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wipriss.Modules.Converter;

/// <summary>
/// Lays the previews out so that they are as big as possible and all fit in the visible area: one photo fills it,
/// three share it, ten and more get small tiles. Only when even small tiles don't fit does the list scroll.
/// Must sit in a <see cref="ScrollViewer"/> (it reads the visible height from it).
/// </summary>
internal sealed class PreviewGrid : Panel
{
    public const double Gap = 10;

    /// <summary>Height of the caption under each picture.</summary>
    public const double Label = 36;

    /// <summary>The smallest tile width before the list starts to scroll.</summary>
    public const double MinTile = 150;

    private ScrollViewer? _viewer;

    /// <summary>The size of one tile after the last layout.</summary>
    public Size Tile { get; private set; }

    /// <summary>The layout changed tile size (the window loads sharper previews when tiles grow).</summary>
    public event Action? TileChanged;

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (_viewer is not null) _viewer.SizeChanged -= Viewer_SizeChanged;
        _viewer = null;
        for (DependencyObject? at = this; at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is ScrollViewer viewer) { _viewer = viewer; break; }
        if (_viewer is not null) _viewer.SizeChanged += Viewer_SizeChanged;
    }

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e) => InvalidateMeasure();

    /// <summary>Columns and tile size for <paramref name="count"/> tiles in a <paramref name="width"/> × <paramref name="height"/> area.</summary>
    public static (int Columns, Size Tile) Fit(int count, double width, double height)
    {
        if (count <= 0 || width <= 0) return (1, new Size(0, 0));
        if (double.IsInfinity(height) || height <= 0) height = width;

        int bestColumns = 1;
        double bestScore = -1;
        Size bestTile = default;
        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (count + columns - 1) / columns;
            double w = (width - Gap * (columns - 1)) / columns;
            double h = (height - Gap * (rows - 1)) / rows;
            if (w <= 0 || h <= Label) continue;
            // Photos are about 4:3: the score is the width a 4:3 picture would get in this cell.
            double score = Math.Min(w, (h - Label) * 4 / 3);
            if (score > bestScore)
            {
                bestScore = score;
                bestColumns = columns;
                bestTile = new Size(w, h);
            }
        }

        if (bestScore >= MinTile) return (bestColumns, bestTile);

        // Too many to fit: small tiles in as many columns as the width allows, and the list scrolls.
        int fitColumns = Math.Max(1, (int)((width + Gap) / (MinTile + Gap)));
        double tileWidth = (width - Gap * (fitColumns - 1)) / fitColumns;
        return (fitColumns, new Size(tileWidth, tileWidth * 3 / 4 + Label));
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? (_viewer?.ViewportWidth ?? 600) : available.Width;
        double height = _viewer is { ViewportHeight: > 1 } viewer ? viewer.ViewportHeight - 1 : available.Height;
        var (columns, tile) = Fit(InternalChildren.Count, width, height);
        foreach (UIElement child in InternalChildren) child.Measure(tile);
        int rows = (InternalChildren.Count + columns - 1) / columns;
        if (tile != Tile)
        {
            Tile = tile;
            Dispatcher.BeginInvoke(() => TileChanged?.Invoke());
        }
        return new Size(width, rows == 0 ? 0 : rows * tile.Height + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (columns, tile) = Fit(InternalChildren.Count, final.Width, _viewer is { ViewportHeight: > 1 } v ? v.ViewportHeight - 1 : final.Height);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int row = i / columns, column = i % columns;
            InternalChildren[i].Arrange(new Rect(column * (tile.Width + Gap), row * (tile.Height + Gap), tile.Width, tile.Height));
        }
        return final;
    }
}
