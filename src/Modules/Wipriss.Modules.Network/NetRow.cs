using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Wipriss.Core.Text;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Network;

internal enum AskMode { None, Stop, Admin }

/// <summary>One program in the panel. Updated in place on every refresh, so an open question survives it.</summary>
internal sealed class NetRow(string key) : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Key { get; } = key;
    public AppReport Report { get; private set; } = null!;
    public bool IsTrusted { get; private set; }

    public string Name => Report.Name;
    public string PathText => Report.Path ?? (Report.IsStoreApp ? "Приложение из Microsoft Store" : "");
    public bool IsFlagged => !IsTrusted && Report.Verdict.Level != ThreatLevel.None;
    public bool IsRunning => Report.Processes.Count > 0;

    public string LevelText => Report.Verdict.Level == ThreatLevel.High ? "очень подозрительно" : "подозрительно";
    public Brush LevelBrush => Brush(Report.Verdict.Level == ThreatLevel.High ? "DangerBrush" : "WarnBrush");
    public IReadOnlyList<string> Reasons => Report.Verdict.Reasons.Select(r => r.Text).ToList();

    public string TrafficText =>
        Report.TotalToday == 0 ? "сегодня ещё не учтено" : $"↓ {TrafficCheck.Bytes(Report.ReceivedToday)}   ↑ {TrafficCheck.Bytes(Report.SentToday)}";

    /// <summary>Second line in the full list: what is happening now and who signed it.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Report.IsWindows && Report.Path is null) parts.Add("системные службы");
            else if (!IsRunning) parts.Add("сейчас не запущен");
            else parts.Add(Report.ConnectionCount == 0 ? "без соединений" : RuText.Count(Report.ConnectionCount, "соединение", "соединения", "соединений"));

            if (Report.Signature is { State: SignatureState.Signed, Publisher: { } publisher }) parts.Add(publisher);
            else if (Report.Signature?.State == SignatureState.Unsigned) parts.Add("без подписи");
            if (IsTrusted) parts.Add("доверяю");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Facts line under the reasons on a suspicious card.</summary>
    public string Facts
    {
        get
        {
            var parts = new List<string> { Report.TotalToday == 0 ? "сегодня ещё не учтено" : $"сегодня ↓ {TrafficCheck.Bytes(Report.ReceivedToday)}  ↑ {TrafficCheck.Bytes(Report.SentToday)}" };
            if (IsRunning)
            {
                parts.Add(RuText.Count(Report.ConnectionCount, "соединение", "соединения", "соединений"));
                if (Report.CpuPercent is { } cpu) parts.Add($"ЦП {cpu:0} %");
            }
            else
            {
                parts.Add("сейчас не запущен");
            }

            parts.Add(Report.Signature?.State switch
            {
                SignatureState.Signed => Report.Signature.Publisher is { } p ? $"подписан: {p}" : "подписан",
                SignatureState.Unsigned => "без подписи",
                SignatureState.Broken => "подпись не сходится",
                _ => Report.IsStoreApp ? "из Microsoft Store" : "подпись не проверена",
            });
            return string.Join(" · ", parts);
        }
    }

    public Visibility FlagVisibility => IsFlagged ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderVisibility => Report.Path is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StopVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; Raise(); }
    }

    public AskMode Mode { get; private set; }
    public bool IsAsking => Mode != AskMode.None;
    public string ConfirmText { get; private set; } = "";
    public string PrimaryText { get; private set; } = "";
    public IReadOnlyList<ProcessRef> PendingAdmin { get; private set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(AppReport report, bool trusted)
    {
        Report = report;
        IsTrusted = trusted;
        Raise(string.Empty); // everything may have changed
    }

    public void AskStop()
    {
        int count = Report.Processes.Count;
        ConfirmText = count == 1
            ? $"Завершить «{Name}»? Несохранённое в программе пропадёт."
            : $"Завершить «{Name}»: {RuText.Count(count, "процесс", "процесса", "процессов")}? Несохранённое пропадёт.";
        PrimaryText = "Завершить";
        PendingAdmin = [];
        SetMode(AskMode.Stop);
    }

    public void AskAdmin(IReadOnlyList<ProcessRef> targets)
    {
        ConfirmText = "Программа запущена от имени администратора. Windows спросит разрешение, Wipriss сам прав администратора не получает.";
        PrimaryText = "Завершить с правами администратора";
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
        Raise(nameof(Mode));
        Raise(nameof(IsAsking));
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
