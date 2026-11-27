using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Wipriss.Modules.Media;

/// <summary>The home-screen card: cover, title, who plays it, and previous / play-pause / next.</summary>
internal sealed class MediaCard : UserControl
{
    public MediaCard(MediaModule module)
    {
        if (module.Current is not { } now) return;

        var root = new DockPanel();
        Content = root;

        // Cover, or a quiet note when the program gives none.
        var cover = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0),
            Background = Res("SocketFillBrush"), VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true,
        };
        if (now.Cover is { } image)
            cover.Child = new Rectangle { RadiusX = 9, RadiusY = 9, Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill } };
        else
            cover.Child = new Viewbox { Width = 20, Height = 20, Child = new Path { Data = MediaModule.Note, Fill = Res("SubtleTextBrush"), Width = 24, Height = 24 } };
        DockPanel.SetDock(cover, Dock.Left);
        root.Children.Add(cover);

        var info = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 6, 0, 0) };
        DockPanel.SetDock(controls, Dock.Bottom);
        controls.Children.Add(Button("", "Назад", now.CanPrevious, module.Previous));
        controls.Children.Add(Button(now.Playing ? "" : "", now.Playing ? "Пауза" : "Играть", true, module.PlayPause));
        controls.Children.Add(Button("", "Дальше", now.CanNext, module.Next));
        info.Children.Add(controls);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        string source = now.Artist.Length > 0 && now.App.Length > 0 ? $"{now.Artist} · {now.App}" : now.Artist + now.App;
        text.Children.Add(new TextBlock
        {
            Text = now.Playing ? "Сейчас играет" : "На паузе", FontSize = 11, Margin = new Thickness(0, 0, 0, 3),
            Foreground = Res("FaintTextBrush"), FontFamily = (FontFamily)Application.Current.FindResource("SmallFont"),
        });
        text.Children.Add(new TextBlock
        {
            Text = now.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 17,
        });
        if (source.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = source, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), Foreground = Res("SubtleTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        info.Children.Add(text);
        root.Children.Add(info);
    }

    private static Button Button(string glyph, string tip, bool enabled, Action click)
    {
        var b = new Button
        {
            Content = glyph, ToolTip = tip, IsEnabled = enabled, FontSize = 14,
            Style = (Style)Application.Current.FindResource("IconButton"),
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
