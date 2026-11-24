using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Wipriss.Core.Text;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Ports;

internal enum AskMode { None, Stop, Admin }

/// <summary>One port in the list. Updated in place on each refresh, so an open question survives it.</summary>
internal sealed class PortRow(string key) : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Key { get; } = key;
    public PortItem Item { get; private set; } = null!;

    public string PortText => Item.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Title => Item.ShortCommand;
    public string? FullCommand => Item.CommandLine ?? Item.Path;

    public string Details
    {
        get
        {
            var parts = new List<string> { $"PID {Item.Pid}" };
            if (Item.Process is { CreateTime: > 0 } p) parts.Add(RuText.Age(DateTime.UtcNow - p.StartedUtc));
            parts.Add(ScopeText(Item.Addresses));
            return string.Join(" · ", parts);
        }
    }

    public bool CanStop => ProcessGuard.WhyNotStop(Item.Kind) is null;
    public Visibility StopVisibility => CanStop ? Visibility.Visible : Visibility.Collapsed;
    public string? Tag => ProcessGuard.Tag(Item.Kind);
    public Visibility TagVisibility => Tag is null ? Visibility.Collapsed : Visibility.Visible;
    public string? WhyNot => ProcessGuard.WhyNotStop(Item.Kind);

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; Raise(); }
    }

    public AskMode Mode { get; private set; }
    public bool IsAsking => Mode != AskMode.None;
    public string ConfirmText { get; private set; } = "";
    public string PrimaryText { get; private set; } = "";
    public string LauncherText { get; private set; } = "";
    public Visibility LauncherVisibility { get; private set; } = Visibility.Collapsed;
    public IReadOnlyList<ProcessRef> PendingAdmin { get; private set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(PortItem item)
    {
        Item = item;
        Raise(nameof(Title));
        Raise(nameof(FullCommand));
        Raise(nameof(Details));
        Raise(nameof(StopVisibility));
        Raise(nameof(Tag));
        Raise(nameof(TagVisibility));
        Raise(nameof(WhyNot));
    }

    public void AskStop(int children)
    {
        string what = children > 0
            ? $"«{Item.ShortCommand}» и {RuText.Count(children, "дочерний процесс", "дочерних процесса", "дочерних процессов")}"
            : $"«{Item.ShortCommand}»";
        ConfirmText = $"Остановить {what}? Порт {Item.Port} освободится.";
        PrimaryText = "Остановить";
        LauncherText = $"Вместе с запуском: {Item.LauncherCommand}";
        LauncherVisibility = Item.Launcher is null ? Visibility.Collapsed : Visibility.Visible;
        PendingAdmin = [];
        SetMode(AskMode.Stop);
    }

    public void AskAdmin(IReadOnlyList<ProcessRef> targets)
    {
        ConfirmText = "Программа запущена от имени администратора. Windows спросит разрешение, "
            + "Wipriss сам прав администратора не получает.";
        PrimaryText = "Остановить с правами администратора";
        LauncherVisibility = Visibility.Collapsed;
        PendingAdmin = targets;
        SetMode(AskMode.Admin);
    }

    public void Clear()
    {
        if (Mode == AskMode.None) return;
        PendingAdmin = [];
        SetMode(AskMode.None);
    }

    private void SetMode(AskMode mode)
    {
        Mode = mode;
        Raise(nameof(ConfirmText));
        Raise(nameof(PrimaryText));
        Raise(nameof(LauncherText));
        Raise(nameof(LauncherVisibility));
        Raise(nameof(Mode));
        Raise(nameof(IsAsking));
    }

    /// <summary>"только этот ПК" for localhost, "открыт в сеть" for all interfaces, otherwise the address itself.</summary>
    internal static string ScopeText(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Any(a => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any))) return "открыт в сеть";
        if (addresses.All(IPAddress.IsLoopback)) return "только этот ПК";
        return addresses.First(a => !IPAddress.IsLoopback(a)).ToString();
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
