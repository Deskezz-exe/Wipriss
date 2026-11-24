namespace Wipriss.Core.Text;

public static class RuText
{
    /// <summary>Russian plural: 1 заметка, 2 заметки, 5 заметок, 21 заметка, 11 заметок.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        int lastTwo = Math.Abs(n) % 100;
        int last = lastTwo % 10;
        if (lastTwo is > 10 and < 20) return many;
        if (last == 1) return one;
        if (last is >= 2 and <= 4) return few;
        return many;
    }

    public static string Count(int n, string one, string few, string many) => $"{n} {Plural(n, one, few, many)}";
}
