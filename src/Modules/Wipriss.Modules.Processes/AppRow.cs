using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Wipriss.Core.Text;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Processes;

internal enum AskMode { None, End, Admin }

/// <summary>One program in the list. Updated in place on each refresh, so hover and open questions survive.</summary>
internal sealed class AppRow(string path) : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Path { get; } = path;
    public string Name { get; private set; } = "";
    public IReadOnlyList<ProcessEntry> Processes { get; private set; } = [];
    public long PrivateBytes { get; private set; }
    public double Cpu { get; private set; }

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; Raise(); }
    }

    public string CountText => Processes.Count > 1 ? $"×{Processes.Count}" : "";
    public string MemoryText => RuText.Memory(PrivateBytes);
    public string CpuText { get; private set; } = "";

    public AskMode Mode { get; private set; }
    public bool IsAsking => Mode != AskMode.None;
    public string ConfirmText { get; private set; } = "";
    public string PrimaryText { get; private set; } = "";
    public string TreeText { get; private set; } = "";
    public Visibility TreeVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>Processes that need the admin helper, after a normal stop was refused.</summary>
    public IReadOnlyList<ProcessRef> PendingAdmin { get; private set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(AppGroup group)
    {
        int oldCount = Processes.Count;
        Name = group.Name;
        Processes = group.Processes;
        PrivateBytes = group.PrivateBytes;
        Cpu = group.CpuPercent ?? 0;
        CpuText = group.CpuPercent switch
        {
            null => "",
            < 0.05 => "0 %",
            < 10 => group.CpuPercent.Value.ToString("0.0", Ru) + " %",
            _ => group.CpuPercent.Value.ToString("0", Ru) + " %",
        };

        Raise(nameof(Name));
        Raise(nameof(MemoryText));
        Raise(nameof(CpuText));
        if (oldCount != Processes.Count) Raise(nameof(CountText));
    }

    public void AskEnd(int extraChildren)
    {
        int count = Processes.Count;
        ConfirmText = count == 1
            ? $"Завершить «{Name}»? Несохранённое в программе пропадёт."
            : $"Завершить «{Name}»: {RuText.Count(count, "процесс", "процесса", "процессов")}? Несохранённое пропадёт.";
        PrimaryText = "Завершить";
        TreeText = $"С дочерними (+{extraChildren})";
        TreeVisibility = extraChildren > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingAdmin = [];
        SetMode(AskMode.End);
    }

    public void AskAdmin(IReadOnlyList<ProcessRef> targets)
    {
        ConfirmText = $"{RuText.Count(targets.Count, "процесс запущен", "процесса запущены", "процессов запущены")} от имени администратора. "
            + "Windows спросит разрешение, Wipriss сам прав администратора не получает.";
        PrimaryText = "Завершить с правами администратора";
        TreeVisibility = Visibility.Collapsed;
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
        Raise(nameof(TreeText));
        Raise(nameof(TreeVisibility));
        Raise(nameof(Mode));
        Raise(nameof(IsAsking));
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
}
