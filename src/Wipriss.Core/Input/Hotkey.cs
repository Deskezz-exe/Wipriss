namespace Wipriss.Core.Input;

/// <summary>
/// A global key combination such as Win+Alt+N, kept as text in settings.
/// Modifier flags match RegisterHotKey (Alt=1, Ctrl=2, Shift=4, Win=8); <see cref="Key"/> is a virtual-key code.
/// </summary>
public readonly record struct Hotkey(int Modifiers, int Key)
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public bool IsEmpty => Key == 0;

    /// <summary>A combination needs a modifier other than Shift alone, so normal typing never triggers it.</summary>
    public bool IsUsable => Key != 0 && (Modifiers & (Alt | Ctrl | Win)) != 0;

    public const int PrintScreen = 0x2C;

    /// <summary>The default screenshot key.</summary>
    public static Hotkey PrtSc => new(0, PrintScreen);

    /// <summary>Keys that are never typed in text, so they may work alone: PrtSc, Pause, Scroll Lock, F1–F24.</summary>
    public bool IsUsableAlone => Key is PrintScreen or 0x13 or 0x91 or >= 0x70 and <= 0x87;

    /// <summary>Good for a key that only Wipriss's keyboard hook catches: a lone special key, or one with Ctrl, Alt or Win.</summary>
    public bool IsUsableForHook => IsUsable || IsUsableAlone;

    public override string ToString()
    {
        if (Key == 0) return "";
        var parts = new List<string>(4);
        if ((Modifiers & Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & Win) != 0) parts.Add("Win");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        int mods = 0, key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": mods |= Ctrl; break;
                case "alt": mods |= Alt; break;
                case "shift": mods |= Shift; break;
                case "win": mods |= Win; break;
                default: key = KeyCode(raw); break;
            }
        }
        return key == 0 ? default : new Hotkey(mods, key);
    }

    private static string KeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x20 => "Space",
        PrintScreen => "PrtSc",
        0x13 => "Pause",
        0x91 => "ScrollLock",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0xC0 => "`",
        0xBA => ";",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xDE => "'",
        0xDB => "[",
        0xDD => "]",
        _ => "0x" + vk.ToString("X2"),
    };

    private static readonly (string, int)[] Named =
        [("PrtSc", PrintScreen), ("Pause", 0x13), ("ScrollLock", 0x91), ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23)];

    private static int KeyCode(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            return c switch { '`' => 0xC0, ';' => 0xBA, ',' => 0xBC, '.' => 0xBE, '/' => 0xBF, '\'' => 0xDE, '[' => 0xDB, ']' => 0xDD, _ => 0 };
        }
        if (name.Equals("Space", StringComparison.OrdinalIgnoreCase)) return 0x20;
        foreach (var (n, code) in Named)
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return code;
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24)
            return 0x6F + f;
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int hex))
            return hex;
        return 0;
    }
}
