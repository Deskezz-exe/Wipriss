using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wipriss.Core.Modules;
using Wipriss.Voice;

namespace Wipriss.Modules.Voice;

public partial class VoicePanel : UserControl
{
    private readonly VoiceModule _module;
    private bool _showing;
    private bool _capturing;

    internal VoicePanel(VoiceModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        foreach (int minutes in VoiceSettings.UnloadChoices)
        {
            var choice = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "Minutes", Content = $"{minutes} мин", Tag = minutes };
            choice.Checked += Minutes_Changed;
            MinutesPanel.Children.Add(choice);
        }
        Loaded += (_, _) =>
        {
            ShowAll();
            if (_module.Download is { } job) Follow(job);
        };
        Unloaded += (_, _) =>
        {
            if (_module.Download is { } job) job.Progress -= OnProgress;
            if (_capturing) { _capturing = false; _module.CancelCapture(); }
        };
    }

    private void ShowAll()
    {
        _showing = true;
        var settings = _module.Settings;
        bool ready = _module.ModelReady;
        DownloadBox.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        ReadyBox.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        EnabledSwitch.IsChecked = settings.Enabled;
        KeepMode.IsChecked = settings.KeepLoaded;
        UnloadMode.IsChecked = !settings.KeepLoaded;
        foreach (RadioButton choice in MinutesPanel.Children) choice.IsChecked = (int)choice.Tag == settings.UnloadMinutes;
        _showing = false;

        bool unload = !settings.KeepLoaded;
        MinutesRow.Visibility = unload ? Visibility.Visible : Visibility.Collapsed;
        MemoryHint.Text = unload
            ? "Модель выгружается, если голосом не пользовались. Первая фраза после перерыва начнётся примерно через секунду."
            : "Текст сразу после нажатия. Модель всё время занимает около 300 МБ памяти.";

        if (!_capturing) KeyText.Text = HotkeyKeys.Describe(settings.Keys);
        ChangeKeyButton.IsEnabled = ready && settings.Enabled && _module.HookActive && !_capturing;

        if (!ready) return;
        if (!settings.Enabled)
            ShowState("FaintTextBrush", "Выключен: клавиша не перехватывается, микрофон и модель не используются.");
        else if (!_module.HookActive)
            ShowState("DangerBrush", "Windows не дала перехватить клавишу. Выключите и включите переключатель.");
        else
            ShowState("OkBrush", $"Нажмите {HotkeyKeys.Describe(settings.Keys)} в любой программе и говорите.");
    }

    private void ShowState(string brush, string text)
    {
        StateDot.Fill = (Brush)FindResource(brush);
        StateText.Text = text;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_module.Download is not null) return;
        var job = new DownloadJob();
        Follow(job);
        var result = await _module.DownloadModelAsync(job);
        job.Progress -= OnProgress;
        if (!IsLoaded) return;
        ShowDownloading(false);
        if (job.Token.IsCancellationRequested) { ShowDownloadText("Скачивание остановлено."); return; }
        if (result == VoiceError.None && _module.ModelReady) { ShowAll(); return; }
        ShowDownloadText(result switch
        {
            VoiceError.Network => "Не удалось скачать: нет связи с сервером. Проверьте интернет и попробуйте ещё раз.",
            VoiceError.ModelDamaged => "Файл пришёл повреждённым и удалён. Попробуйте ещё раз.",
            VoiceError.Disk => "Не удалось записать на диск. Проверьте, хватает ли места (нужно 232 МБ).",
            _ => "Не получилось скачать модель. Попробуйте ещё раз.",
        });
    }

    private void Follow(DownloadJob job)
    {
        job.Progress += OnProgress;
        ShowDownloading(true);
        OnProgress(job.Percent);
    }

    private void OnProgress(int percent)
    {
        ProgressFill.Width = Math.Max(0, ProgressBox.ActualWidth) * percent / 100.0;
        ShowDownloadText($"Скачано {percent}% из 232 МБ");
    }

    private void ShowDownloading(bool on)
    {
        DownloadButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ProgressBox.Visibility = CancelButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) ProgressBox.UpdateLayout();
    }

    private void ShowDownloadText(string text)
    {
        DownloadText.Text = text;
        DownloadText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _module.Download?.Cancel();

    private void Enabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_showing) return;
        _module.Settings.Enabled = EnabledSwitch.IsChecked == true;
        _module.SaveSettings();
        ShowAll();
    }

    private void ChangeKey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) return;
        if (!_module.BeginCapture(OnCaptured)) return;
        _capturing = true;
        ChangeKeyButton.IsEnabled = false;
        KeyText.Text = "Нажмите клавиши…";
        KeyHint.Text = "Зажмите 1, 2 или 3 клавиши вместе и отпустите. Esc отменяет.";
    }

    private void OnCaptured(List<int>? keys)
    {
        _capturing = false;
        KeyHint.Text = "Одна, две или три клавиши вместе, например «Правый Ctrl» или «Ctrl + Alt + Пробел».";
        if (IsLoaded) ShowAll();
    }

    private void Memory_Changed(object sender, RoutedEventArgs e)
    {
        if (_showing) return;
        _module.Settings.KeepLoaded = KeepMode.IsChecked == true;
        _module.SaveSettings();
        ShowAll();
    }

    private void Minutes_Changed(object sender, RoutedEventArgs e)
    {
        if (_showing || sender is not RadioButton { Tag: int minutes }) return;
        _module.Settings.UnloadMinutes = minutes;
        _module.SaveSettings();
    }
}
