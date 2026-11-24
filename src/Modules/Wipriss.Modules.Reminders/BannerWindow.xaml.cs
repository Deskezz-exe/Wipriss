using System.Windows;
using System.Windows.Media;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Reminders;

/// <summary>
/// The reminder banner: under the island or in the bottom-right corner.
/// It never takes focus, so it won't interrupt typing or a game. It stays until you dismiss it.
/// </summary>
public partial class BannerWindow : Window
{
    private readonly BannerPlacement _placement;
    private readonly int _slot;

    internal BannerWindow(Reminder reminder, bool missed, bool preview, BannerPlacement placement, int slot)
    {
        InitializeComponent();
        _placement = placement;
        _slot = slot;

        Caption.Text = preview ? "Пример" : missed ? "Пропущенное напоминание" : "Напоминание";
        TitleText.Text = reminder.Title;
        WhenText.Text = ReminderInput.Describe(reminder.At, DateTime.Now);
        if (preview) SnoozeButton.Visibility = Visibility.Collapsed;

        Loaded += (_, _) => Place();
    }

    /// <summary>Minutes to snooze.</summary>
    public event Action<int>? SnoozeRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.NoActivateToolWindow(this);
        NativeWindowStyle.RoundCorners(this);
        NativeWindowStyle.FrameColors(this, (Color)FindResource("HairlineColor"));
    }

    private void Place()
    {
        double height = ActualHeight;
        if (_placement == BannerPlacement.Corner)
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 16;
            Top = work.Bottom - height - 16 - _slot * (height + 10);
        }
        else
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 48 + _slot * (height + 10);
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        SnoozeRequested?.Invoke(10);
        Close();
    }
}
