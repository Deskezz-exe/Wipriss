using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wipriss.Modules.Reminders;

public partial class RemindersPanel : UserControl
{
    private Brush ErrorBrush => (Brush)FindResource("DangerBrush");
    private Brush WarnBrush => (Brush)FindResource("WarnBrush");
    private Brush OkBrush => (Brush)FindResource("OkBrush");

    private readonly RemindersModule _module;

    internal RemindersPanel(RemindersModule module)
    {
        InitializeComponent();
        _module = module;

        SoundToggle.IsChecked = module.Data.Sound;
        (module.Data.Banner switch
        {
            BannerPlacement.Corner => PlaceCorner,
            BannerPlacement.None => PlaceNone,
            _ => PlaceCenter,
        }).IsChecked = true;

        // Subscribe after the initial values are set, so opening the panel saves nothing.
        SoundToggle.Checked += (_, _) => _module.SetSound(true);
        SoundToggle.Unchecked += (_, _) => _module.SetSound(false);
        PlaceCenter.Checked += (_, _) => _module.SetPlacement(BannerPlacement.Center);
        PlaceCorner.Checked += (_, _) => _module.SetPlacement(BannerPlacement.Corner);
        PlaceNone.Checked += (_, _) => _module.SetPlacement(BannerPlacement.None);

        Loaded += (_, _) =>
        {
            _module.Changed += Refresh;
            Refresh();
            UpdatePreview();
            TitleBox.Focus();
        };
        Unloaded += (_, _) => _module.Changed -= Refresh;
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var upcoming = _module.Upcoming.Select(r => new ReminderView(r, ReminderInput.Describe(r.At, now))).ToList();
        var past = _module.Past.Select(r => new ReminderView(r, ReminderInput.Describe(r.At, now))).ToList();

        UpcomingList.ItemsSource = upcoming;
        PastList.ItemsSource = past;
        NoUpcoming.Visibility = upcoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PastHeader.Visibility = past.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SilentHint.Visibility = !_module.Data.Sound && _module.Data.Banner == BannerPlacement.None
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Reads the fields. Returns the moment, or null with a message explaining what's wrong.</summary>
    private DateTime? ReadMoment(out string message, out bool isError)
    {
        var now = DateTime.Now;
        isError = true;

        if (string.IsNullOrWhiteSpace(TimeBox.Text))
        {
            message = "Укажите время, например 21:00.";
            isError = false;
            return null;
        }
        if (!ReminderInput.TryParseDate(DateBox.Text, now, out var date))
        {
            message = "Не понимаю дату. Подойдёт: сегодня, завтра, 30.11, 30.11.2026.";
            return null;
        }
        if (!ReminderInput.TryParseTime(TimeBox.Text, out var time))
        {
            message = "Не понимаю время. Подойдёт: 21:00, 2100, 9.";
            return null;
        }

        var at = date + time;
        if (at <= now)
        {
            message = "Это время уже прошло.";
            return null;
        }

        message = "Напомню: " + ReminderInput.Describe(at, now);
        isError = false;
        return at;
    }

    private void UpdatePreview()
    {
        var at = ReadMoment(out var message, out bool isError);
        PreviewText.Text = message;
        PreviewText.Foreground = at is not null ? (Brush)FindResource("SubtleTextBrush") : isError ? ErrorBrush : WarnBrush;
    }

    private void Add()
    {
        var at = ReadMoment(out var message, out _);
        if (at is null)
        {
            UpdatePreview();
            return;
        }
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            PreviewText.Text = "Напишите, о чём напомнить.";
            PreviewText.Foreground = WarnBrush;
            TitleBox.Focus();
            return;
        }

        _module.Add(TitleBox.Text, at.Value);
        TitleBox.Clear();
        TimeBox.Clear();
        DateBox.Clear();
        PreviewText.Text = "Добавлено. " + message;
        PreviewText.Foreground = OkBrush;
        TitleBox.Focus();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Add();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Add();

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var now = DateTime.Now;
        DateTime at = tag switch
        {
            "10m" => now.AddMinutes(10),
            "1h" => now.AddHours(1),
            _ => now.Date.AddDays(1).AddHours(9),
        };

        DateBox.Text = at.Date == now.Date ? "сегодня" : at.Date == now.Date.AddDays(1) ? "завтра" : at.ToString("dd.MM.yyyy");
        TimeBox.Text = at.ToString("HH:mm");
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) TitleBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReminderView view }) _module.Delete(view.Reminder);
    }

    private void ClearPast_Click(object sender, RoutedEventArgs e) => _module.ClearPast();

    private void Test_Click(object sender, RoutedEventArgs e) => _module.ShowPreview();
}

/// <summary>One row of the list.</summary>
public sealed class ReminderView(Reminder reminder, string when)
{
    public Reminder Reminder { get; } = reminder;
    public string Title => Reminder.Title;
    public string When { get; } = when;
}
