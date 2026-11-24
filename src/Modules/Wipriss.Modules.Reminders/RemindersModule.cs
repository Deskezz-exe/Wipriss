using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Storage;
using Wipriss.Core.Timing;

namespace Wipriss.Modules.Reminders;

public enum BannerPlacement { Center, Corner, None }

public sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";

    /// <summary>Local wall-clock time.</summary>
    public DateTime At { get; set; }

    public bool Done { get; set; }
}

public sealed class RemindersFile
{
    public List<Reminder> Items { get; set; } = [];
    public bool Sound { get; set; } = true;
    public BannerPlacement Banner { get; set; } = BannerPlacement.Center;
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemindersFile))]
internal sealed partial class ReminderJson : JsonSerializerContext;

/// <summary>
/// Reminders. One Windows waitable timer is set to the nearest reminder, so nothing is checked
/// every second. Reminders missed while the PC was off show up on the next start.
/// </summary>
public sealed class RemindersModule : IModule
{
    private const int KeepPast = 10;
    private static readonly string FilePath = Path.Combine(AppPaths.LocalDir, "reminders.dat");

    private readonly List<BannerWindow> _banners = [];
    private RemindersFile _data = new();
    private AbsoluteTimer? _timer;
    private IModuleHost? _host;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("reminders");

    public Size PanelSize => new(440, 620);

    public string Summary
    {
        get
        {
            var next = Upcoming.FirstOrDefault();
            return next is null ? "нет напоминаний" : ReminderInput.Short(next.At, DateTime.Now);
        }
    }

    /// <summary>Raised on the UI thread when the list or settings change.</summary>
    public event Action? Changed;

    internal RemindersFile Data => _data;

    internal IEnumerable<Reminder> Upcoming => _data.Items.Where(r => !r.Done).OrderBy(r => r.At);

    internal IEnumerable<Reminder> Past => _data.Items.Where(r => r.Done).OrderByDescending(r => r.At);

    public void Start(IModuleHost host)
    {
        _host = host;
        _data = SecureStore.ReadJson(FilePath, ReminderJson.Default.RemindersFile) ?? new RemindersFile();
        _timer = new AbsoluteTimer(() => host.Dispatcher.BeginInvoke(OnTimer));
        host.TimeChanged += OnTimeChanged;
        FireDue();
        Reschedule();
    }

    public void Stop()
    {
        if (_host is not null) _host.TimeChanged -= OnTimeChanged;
        _timer?.Dispose();
        _timer = null;
        foreach (var banner in _banners.ToList()) banner.Close();
        _banners.Clear();
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new RemindersPanel(this);

    // ---------- timing ----------

    private void OnTimer()
    {
        FireDue();
        Reschedule();
    }

    private void OnTimeChanged()
    {
        TimeZoneInfo.ClearCachedData();
        FireDue();
        Reschedule();
    }

    private void FireDue()
    {
        var now = DateTime.Now;
        var due = _data.Items.Where(r => !r.Done && r.At <= now.AddSeconds(1)).OrderBy(r => r.At).ToList();
        if (due.Count == 0) return;

        foreach (var reminder in due) reminder.Done = true;
        TrimPast();
        Save();

        if (_data.Sound) PlaySound();
        foreach (var reminder in due) ShowBanner(reminder, missed: now - reminder.At > TimeSpan.FromMinutes(2), preview: false);
        Changed?.Invoke();
    }

    private void Reschedule()
    {
        if (_timer is null) return;
        var next = Upcoming.FirstOrDefault();
        if (next is null) _timer.Cancel();
        else _timer.Schedule(ToUtc(next.At));
    }

    private static DateTime ToUtc(DateTime local)
    {
        var wallClock = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(wallClock, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            // The time doesn't exist (clocks jumped forward): use the first moment after the jump.
            return TimeZoneInfo.ConvertTimeToUtc(wallClock.AddHours(1), TimeZoneInfo.Local);
        }
    }

    // ---------- actions ----------

    internal void Add(string title, DateTime at)
    {
        _data.Items.Add(new Reminder { Title = title.Trim(), At = at });
        Commit();
    }

    internal void Delete(Reminder reminder)
    {
        _data.Items.Remove(reminder);
        Commit();
    }

    internal void ClearPast()
    {
        _data.Items.RemoveAll(r => r.Done);
        Commit();
    }

    internal void Snooze(Reminder reminder, int minutes)
    {
        var at = DateTime.Now.AddMinutes(minutes);
        reminder.At = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0);
        reminder.Done = false;
        if (!_data.Items.Contains(reminder)) _data.Items.Add(reminder);
        Commit();
    }

    internal void SetSound(bool on)
    {
        _data.Sound = on;
        Commit();
    }

    internal void SetPlacement(BannerPlacement placement)
    {
        _data.Banner = placement;
        Commit();
    }

    /// <summary>Shows what a reminder will look and sound like.</summary>
    internal void ShowPreview()
    {
        if (_data.Sound) PlaySound();
        var sample = new Reminder { Title = "Так будет выглядеть напоминание", At = DateTime.Now };
        ShowBanner(sample, missed: false, preview: true);
    }

    private void ShowBanner(Reminder reminder, bool missed, bool preview)
    {
        if (_data.Banner == BannerPlacement.None) return;

        var banner = new BannerWindow(reminder, missed, preview, _data.Banner, _banners.Count);
        banner.SnoozeRequested += minutes => Snooze(reminder, minutes);
        banner.Closed += (_, _) => _banners.Remove(banner);
        _banners.Add(banner);
        banner.Show();
    }

    private void Commit()
    {
        Save();
        Reschedule();
        Changed?.Invoke();
    }

    private void TrimPast()
    {
        foreach (var old in Past.Skip(KeepPast).ToList()) _data.Items.Remove(old);
    }

    private void Save() => SecureStore.WriteJson(FilePath, _data, ReminderJson.Default.RemindersFile);

    // Plays the "Reminder" sound from the user's Windows sound scheme.
    private static void PlaySound() => PlaySoundW("Notification.Reminder", IntPtr.Zero, SND_ALIAS | SND_ASYNC);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_ALIAS = 0x00010000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(string sound, IntPtr module, uint flags);
}
