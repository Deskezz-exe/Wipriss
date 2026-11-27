using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Voice;

/// <summary>The Voice page in Settings: the key, the models and how long the model stays in memory.</summary>
public partial class VoicePanel : UserControl
{
    private readonly VoiceModule _module;
    private bool _showing;
    private bool _capturing;

    internal VoicePanel(VoiceModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        ModelsHost.Content = new ModelList(module, useOnDownload: false);
        foreach (int minutes in VoiceSettings.UnloadChoices)
        {
            var choice = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "Minutes", Content = $"{minutes} мин", Tag = minutes };
            choice.Checked += Minutes_Changed;
            MinutesPanel.Children.Add(choice);
        }
        Loaded += (_, _) =>
        {
            _module.ModelsChanged += ShowAll;
            ShowAll();
        };
        Unloaded += (_, _) =>
        {
            _module.ModelsChanged -= ShowAll;
            if (_capturing) { _capturing = false; _module.CancelCapture(); }
        };
    }

    private void ShowAll()
    {
        _showing = true;
        var settings = _module.Settings;
        KeepMode.IsChecked = settings.KeepLoaded;
        UnloadMode.IsChecked = !settings.KeepLoaded;
        foreach (RadioButton choice in MinutesPanel.Children) choice.IsChecked = (int)choice.Tag == settings.UnloadMinutes;
        _showing = false;

        bool unload = !settings.KeepLoaded;
        MinutesRow.Visibility = unload ? Visibility.Visible : Visibility.Collapsed;
        MemoryHint.Text = unload
            ? "После перерыва первая фраза начнётся примерно через секунду."
            : "Текст сразу, но модель всё время занимает память (до 400 МБ).";

        if (!_capturing) KeyText.Text = HotkeyKeys.Describe(settings.Keys);
        ChangeKeyButton.IsEnabled = _module.HookActive && !_capturing;

        if (!_module.HookActive)
            ShowState("DangerBrush", "Windows не дала перехватить клавишу. Выключите и включите голос.");
        else if (!_module.ModelReady)
            ShowState("WarnBrush", "Скачайте модель ниже, или нажмите клавишу, и Wipriss предложит выбрать.");
        else
            ShowState("OkBrush", $"Готово. Модель: {_module.CurrentModel.Title}.");
    }

    private void ShowState(string brush, string text)
    {
        StateDot.Fill = (Brush)FindResource(brush);
        StateText.Text = text;
    }

    private void ChangeKey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) return;
        if (!_module.BeginCapture(OnCaptured)) return;
        _capturing = true;
        ChangeKeyButton.IsEnabled = false;
        KeyText.Text = "Нажмите клавиши…";
        KeyHint.Text = "Зажмите 1–3 клавиши вместе и отпустите. Esc — отмена.";
    }

    private void OnCaptured(List<int>? keys)
    {
        _capturing = false;
        KeyHint.Text = "Нажмите — говорите — нажмите ещё раз. Esc отменяет.";
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
