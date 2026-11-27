using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Voice;

/// <summary>
/// Opens when the dictation key is pressed and no model is downloaded yet: the user picks one,
/// sees what it weighs and downloads it right here.
/// </summary>
internal sealed class ModelChooserWindow : Window
{
    public ModelChooserWindow(VoiceModule module)
    {
        Title = "Голосовой ввод";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        UseLayoutRounding = true;
        Background = Res("WindowBrush");
        Foreground = Res("TextBrush");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var title = new TextBlock { Text = "Выберите модель распознавания", FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 19, FontWeight = FontWeights.SemiBold };
        var subtitle = new TextBlock
        {
            Text = "Скачивается один раз, дальше всё работает без интернета. Голос не покидает компьютер.",
            Style = (Style)FindResource("Caption"), FontSize = 12.5, Margin = new Thickness(0, 5, 0, 16),
        };
        root.Children.Add(title);
        root.Children.Add(subtitle);

        var list = new ModelList(module, useOnDownload: true);
        root.Children.Add(list);

        var close = new Button { Content = "Позже", Style = (Style)FindResource("PlainButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        list.Downloaded += () =>
        {
            title.Text = "Готово";
            subtitle.Text = $"Нажмите {HotkeyKeys.Describe(module.Settings.Keys)} в любой программе и говорите.";
            close.Content = "Закрыть";
        };

        Content = root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.DarkTitleBar(this);
        NativeWindowStyle.FrameColors(this, (Color)FindResource("HairlineColor"), (Color)FindResource("WindowColor"), (Color)FindResource("TextColor"));
    }

    private Brush Res(string key) => (Brush)FindResource(key);
}
