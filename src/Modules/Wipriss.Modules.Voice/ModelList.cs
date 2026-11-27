using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wipriss.Voice;

namespace Wipriss.Modules.Voice;

/// <summary>
/// The speech models as rows: what each understands, how fast and heavy it is, and Download / Use / Remove.
/// Shown in Settings and in the window that opens when the hotkey is pressed without a model.
/// </summary>
internal sealed class ModelList : StackPanel
{
    private readonly VoiceModule _module;
    private readonly bool _useOnDownload;
    private readonly Dictionary<string, string> _errors = [];
    private DownloadJob? _followed;
    private Border? _progressFill;
    private TextBlock? _progressText;

    /// <param name="useOnDownload">The downloaded model becomes the one in use (the "no model" window).</param>
    public ModelList(VoiceModule module, bool useOnDownload)
    {
        _module = module;
        _useOnDownload = useOnDownload;
        Loaded += (_, _) =>
        {
            _module.ModelsChanged += Rebuild;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            _module.ModelsChanged -= Rebuild;
            Unfollow();
        };
    }

    /// <summary>A model finished downloading (for the "no model" window to close itself).</summary>
    public event Action? Downloaded;

    private void Rebuild()
    {
        Unfollow();
        Children.Clear();
        var job = _module.Download;
        foreach (var model in VoiceModels.All)
            Children.Add(Row(model, job));
        if (job is not null) Follow(job);
    }

    private FrameworkElement Row(VoiceModel model, DownloadJob? job)
    {
        bool installed = VoiceModule.IsInstalled(model);
        bool current = installed && _module.Settings.Model == model.Id;
        bool downloading = job?.Model == model.Id;

        var card = new Border
        {
            Style = (Style)FindResource("ListCard"),
            Padding = new Thickness(14, 11, 12, 11),
            BorderBrush = current ? Res("LineStrongBrush") : Res("DividerBrush"),
        };
        var body = new DockPanel();
        card.Child = body;

        // Right: the action.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(actions, Dock.Right);
        if (downloading)
        {
            actions.Children.Add(Button("Отмена", "PlainButton", () => _module.Download?.Cancel()));
        }
        else if (current)
        {
            actions.Children.Add(new TextBlock { Text = "Используется", Foreground = Res("OkBrush"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        }
        else if (installed)
        {
            actions.Children.Add(Button("Выбрать", "PlainButton", () => _module.SelectModel(model)));
            var remove = Button("", "IconButton", () => Remove(model));
            remove.ToolTip = "Удалить с диска";
            remove.Margin = new Thickness(4, 0, 0, 0);
            actions.Children.Add(remove);
        }
        else
        {
            var download = Button("Скачать", _useOnDownload ? "PrimaryButton" : "PlainButton", () => StartDownload(model));
            download.IsEnabled = job is null;
            actions.Children.Add(download);
        }
        body.Children.Add(actions);

        // Left: name, badges, one line about it.
        var info = new StackPanel();
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = model.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Badge(model.Speed, model.Speed == "Тяжёлая" ? "WarnBrush" : "OkBrush"));
        title.Children.Add(Badge(model.SizeText, "SubtleTextBrush"));
        title.Children.Add(Badge(model.Languages, "SubtleTextBrush"));
        info.Children.Add(title);
        info.Children.Add(new TextBlock { Text = model.Note, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 5, 0, 0) });

        if (downloading)
        {
            var bar = new Grid { Margin = new Thickness(0, 9, 0, 0), Height = 4 };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Res("SunkenBrush") });
            _progressFill = new Border { CornerRadius = new CornerRadius(2), Background = Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            bar.Children.Add(_progressFill);
            info.Children.Add(bar);
            _progressText = new TextBlock { Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 5, 0, 0) };
            info.Children.Add(_progressText);
        }
        else if (_errors.TryGetValue(model.Id, out var error))
        {
            info.Children.Add(new TextBlock { Text = error, Style = (Style)FindResource("Caption"), Foreground = Res("WarnBrush"), Margin = new Thickness(0, 5, 0, 0) });
        }
        body.Children.Add(info);
        return card;
    }

    private async void StartDownload(VoiceModel model)
    {
        if (_module.Download is not null) return;
        _errors.Remove(model.Id);
        var job = new DownloadJob(model.Id, use: _useOnDownload);
        var result = await _module.DownloadModelAsync(job);
        if (job.Token.IsCancellationRequested) return;
        if (result == VoiceError.None && VoiceModule.IsInstalled(model))
        {
            Downloaded?.Invoke();
            return;
        }
        _errors[model.Id] = result switch
        {
            VoiceError.Network => "Нет связи с сервером. Проверьте интернет и попробуйте снова.",
            VoiceError.ModelDamaged => "Файл пришёл повреждённым и удалён. Попробуйте снова.",
            VoiceError.Disk => $"Не удалось записать на диск. Нужно {model.SizeText} свободного места.",
            _ => "Не получилось скачать. Попробуйте снова.",
        };
        if (IsLoaded) Rebuild();
    }

    private void Remove(VoiceModel model)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"Удалить модель {model.Title} ({model.SizeText}) с диска?\nПотом её можно скачать снова.",
            "Wipriss", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) _module.DeleteModel(model);
    }

    private void Follow(DownloadJob job)
    {
        _followed = job;
        job.Progress += OnProgress;
        Dispatcher.BeginInvoke(() => OnProgress(job.Percent), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Unfollow()
    {
        if (_followed is not null) _followed.Progress -= OnProgress;
        _followed = null;
        _progressFill = null;
        _progressText = null;
    }

    private void OnProgress(int percent)
    {
        if (_progressFill?.Parent is FrameworkElement bar) _progressFill.Width = bar.ActualWidth * percent / 100.0;
        if (_progressText is not null && _followed is { } job)
            _progressText.Text = $"Скачано {percent}% из {VoiceModels.Get(job.Model).SizeText}";
    }

    private Button Button(string text, string style, Action click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(style) };
        b.Click += (_, _) => click();
        return b;
    }

    private Border Badge(string text, string brush) => new()
    {
        Margin = new Thickness(0, 1, 5, 1),
        Padding = new Thickness(7, 1, 7, 2),
        CornerRadius = new CornerRadius(5),
        Background = Res("SunkenBrush"),
        BorderBrush = Res("DividerBrush"),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Res(brush), FontFamily = (FontFamily)FindResource("SmallFont") },
    };

    private Brush Res(string key) => (Brush)FindResource(key);
}
