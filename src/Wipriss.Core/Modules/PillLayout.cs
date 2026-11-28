namespace Wipriss.Core.Modules;

/// <summary>Which blocks fit into the closed pill. Pure arithmetic, so it is covered by tests.</summary>
public static class PillLayout
{
    public readonly record struct Item(PillGroup Group, int Priority, double Width);

    /// <summary>
    /// Keeps as many blocks as fit into <paramref name="maxWidth"/>. While they do not, the block with the lowest
    /// priority goes (on a tie, the one further right). Items come in display order.
    /// </summary>
    /// <param name="gap">Space between two blocks of one group.</param>
    /// <param name="groupGap">Space between groups (it holds the divider).</param>
    public static bool[] Fit(IReadOnlyList<Item> items, double maxWidth, double gap, double groupGap)
    {
        var keep = new bool[items.Count];
        Array.Fill(keep, true);
        while (Width(items, keep, gap, groupGap) > maxWidth)
        {
            int drop = -1;
            for (int i = 0; i < items.Count; i++)
                if (keep[i] && (drop < 0 || items[i].Priority <= items[drop].Priority)) drop = i;
            if (drop < 0) break;
            keep[drop] = false;
        }
        return keep;
    }

    /// <summary>Width of the kept blocks with the space between them.</summary>
    public static double Width(IReadOnlyList<Item> items, bool[] keep, double gap, double groupGap)
    {
        double width = 0;
        int last = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (!keep[i]) continue;
            if (last >= 0) width += items[last].Group == items[i].Group ? gap : groupGap;
            width += items[i].Width;
            last = i;
        }
        return width;
    }
}
