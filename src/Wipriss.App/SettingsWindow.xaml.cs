using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wipriss.App.Interop;
using Wipriss.App.Shell;
using Wipriss.Core;
using Wipriss.Core.Input;
using Wipriss.Core.Modules;
using Wipriss.Core.Settings;
using Wipriss.Core.Ui;

namespace Wipriss.App;

/// <summary>
/// Settings: sections on the left, the chosen one on the right. Created when opened and dropped when
/// closed, so it costs nothing in between. Each page is built when it is chosen.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly ModuleManager _modules;
    private readonly HotkeyService _hotkeys;
    private readonly List<(string Id, string Title, string Glyph, Action Build)> _sections;
    private string _current = "general";
    private Action? _cancelRecording;

    internal SettingsWindow(SettingsStore store, ModuleManager modules, HotkeyService hotkeys)
    {
        InitializeComponent();
        _store = store;
        _modules = modules;
        _hotkeys = hotkeys;

        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "" : $"Версия {version.Major}.{version.Minor}.{version.Build}";

        _sections =
        [
            ("general", "Общие", "", BuildGeneral),
            ("home", "Главный экран", "", BuildHome),
            ("notes", "Заметки", "", BuildNotes),
            ("capture", "Скриншоты", "", () => BuildModulePage("capture", "Снимок экрана по клавише PrtSc.")),
            ("dictation", "Голос", "", () => BuildModulePage("dictation", "Говорите, и текст печатается там, где курсор.")),
            ("about", "О программе", "", BuildAbout),
        ];
        foreach (var (id, title, glyph, _) in _sections)
        {
            var item = new RadioButton { Content = title, Tag = glyph, GroupName = "Nav", Style = St("NavItem"), IsChecked = id == _current };
            item.Checked += (_, _) => Show(id);
            Nav.Children.Add(item);
        }

        _hotkeys.Applied += OnHotkeysApplied;
        Show(_current);
    }

    public event Action? ExitRequested;

    /// <summary>The user picked a new place for the Wipriss folder (already checked).</summary>
    public event Action<string>? MoveFolderRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        NativeWindowStyle.FrameColors(this, ThemeColors.Hairline, caption: ThemeColors.Window, captionText: ThemeColors.Text);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancelRecording?.Invoke();
        _hotkeys.Applied -= OnHotkeysApplied;
        base.OnClosed(e);
    }

    private void OnHotkeysApplied()
    {
        if (_current == "notes" && _cancelRecording is null) Dispatcher.BeginInvoke(() => Show("notes"));
    }

    private void Show(string id)
    {
        _cancelRecording?.Invoke();
        _current = id;
        Page.Children.Clear();
        PageScroll.ScrollToTop();
        try { _sections.First(s => s.Id == id).Build(); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    // ---- Pages ----

    private void BuildGeneral()
    {
        Header("Общие", null);

        var island = Card();
        island.Children.Add(Toggle("Прятать при игре и видео на весь экран", null, _store.Current.HideInFullscreen,
            on => _store.Update(s => s.HideInFullscreen = on)));
        island.Children.Add(Divider());
        island.Children.Add(Toggle("Запускать вместе с Windows", null, Autostart.IsEnabled, Autostart.Set));
        Section("Островок", island);

        var folder = Card();
        var path = new TextBlock { Text = AppPaths.Root, ToolTip = AppPaths.Root, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var problem = Caption("", "WarnBrush");
        problem.Visibility = Visibility.Collapsed;
        problem.Margin = new Thickness(0, 8, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(Button("Открыть", "PlainButton", OpenFolder));
        buttons.Children.Add(WithMargin(Button("Сменить…", "PlainButton", () => ChangeFolder(problem)), new Thickness(6, 0, 0, 0)));
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(path);
        folder.Children.Add(Padded(row));
        folder.Children.Add(problem);
        Section("Папка Wipriss", folder, "Скриншоты, записи и зашифрованные данные программы.");
    }

    private void BuildHome()
    {
        Header("Главный экран", "Открывается, когда вы наводите мышь на островок.");

        var cards = Card();
        bool first = true;
        foreach (var info in ModuleCatalog.All.Where(m => m.Place == ModulePlace.Card))
        {
            if (!first) cards.Children.Add(Divider());
            first = false;
            cards.Children.Add(ModuleToggle(info));
        }
        cards.Children.Add(Divider());
        cards.Children.Add(Toggle("Показывать в капсуле, что играет", "Иначе капсула показывает только работу Claude.",
            _store.Current.MediaInPill, on => _store.Update(s => s.MediaInPill = on)));
        Section("Карточки", cards);

        var dock = Card();
        first = true;
        foreach (var info in ModuleCatalog.All.Where(m => m.Place == ModulePlace.Dock))
        {
            if (!first) dock.Children.Add(Divider());
            first = false;
            dock.Children.Add(ModuleToggle(info));
        }
        Section("Кнопки внизу", dock, "Выключенный модуль не загружается и не тратит память.");
    }

    private void BuildNotes()
    {
        Header("Заметки", "Открываются горячей клавишей из любой программы.");
        var card = Card();
        card.Children.Add(ModuleToggle(ModuleCatalog.Get("notes"), title: "Заметки включены"));
        if (_store.Current.IsModuleEnabled("notes"))
        {
            card.Children.Add(Divider());
            card.Children.Add(HotkeyRow("notes", "Открыть заметки"));
        }
        Section(null, card);
    }

    private void BuildModulePage(string id, string subtitle)
    {
        var info = ModuleCatalog.Get(id);
        Header(info.Title, subtitle);
        var card = Card();
        card.Children.Add(ModuleToggle(info, title: "Включено"));
        Section(null, card);

        if (_modules.Get(id) is not { } module) return;
        try
        {
            var page = module is ISettingsPage settings ? settings.CreateSettings() : module.CreatePanel(new PageContext());
            page.Margin = new Thickness(0, 18, 0, 0);
            Page.Children.Add(page);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private void BuildAbout()
    {
        Header("О программе", null);
        var card = Card();
        card.Children.Add(Padded(new TextBlock
        {
            Text = "Wipriss — островок вверху экрана: работа Claude, музыка, заметки, скриншоты и мелкие утилиты в одном месте. Без рекламы и слежки, бесплатно, открытый код (MIT).",
            TextWrapping = TextWrapping.Wrap, LineHeight = 19,
        }));
        Section(null, card);

        var exit = Button("Выйти из Wipriss", "PlainButton", () => ExitRequested?.Invoke());
        exit.HorizontalAlignment = HorizontalAlignment.Left;
        exit.Margin = new Thickness(0, 18, 0, 0);
        Page.Children.Add(exit);
    }

    // ---- Hotkey recorder ----

    private FrameworkElement HotkeyRow(string moduleId, string title)
    {
        string current = HotkeyService.Current(_store.Current, moduleId);
        var hint = Caption(_hotkeys.IsTaken(moduleId) ? "Это сочетание занято другой программой. Выберите другое." : "Нажмите кнопку, затем нужные клавиши. Esc — отмена.",
            _hotkeys.IsTaken(moduleId) ? "WarnBrush" : "SubtleTextBrush");

        var key = new Button { Style = St("PlainButton"), MinWidth = 130, Content = current.Length > 0 ? Pretty(current) : "Нет", Focusable = true };
        var reset = Button("По умолчанию", "PlainButton", () =>
        {
            _store.Update(s => s.Hotkeys.Remove(moduleId));
            Show(_current);
        });
        reset.Margin = new Thickness(6, 0, 0, 0);
        reset.Visibility = _store.Current.Hotkeys.ContainsKey(moduleId) ? Visibility.Visible : Visibility.Collapsed;

        key.Click += (_, _) =>
        {
            if (_cancelRecording is not null) return;
            key.Content = "Нажмите клавиши…";
            hint.Text = "Например Win + Alt + N. Нужна Ctrl, Alt или Win. Backspace — без клавиши.";
            hint.Foreground = Brush("SubtleTextBrush");
            _hotkeys.Suspend(true);

            void OnKey(object s, KeyEventArgs e)
            {
                e.Handled = true;
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                    return;
                if (k == Key.Escape) { Stop(); Show(_current); return; }
                if (k is Key.Back or Key.Delete)
                {
                    Stop();
                    _store.Update(st => st.Hotkeys[moduleId] = "");
                    Show(_current);
                    return;
                }

                var mods = Keyboard.Modifiers;
                bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
                int flags = (mods.HasFlag(ModifierKeys.Control) ? Hotkey.Ctrl : 0) | (mods.HasFlag(ModifierKeys.Alt) ? Hotkey.Alt : 0)
                            | (mods.HasFlag(ModifierKeys.Shift) ? Hotkey.Shift : 0) | (win ? Hotkey.Win : 0);
                var hk = new Hotkey(flags, KeyInterop.VirtualKeyFromKey(k));
                if (!hk.IsUsable)
                {
                    hint.Text = "Добавьте Ctrl, Alt или Win, чтобы клавиша не мешала печатать.";
                    hint.Foreground = Brush("WarnBrush");
                    return;
                }
                Stop();
                _store.Update(st => st.Hotkeys[moduleId] = hk.ToString());
                Show(_current);
            }

            void OnLost(object s, RoutedEventArgs e) { Stop(); Show(_current); }

            void Stop()
            {
                key.PreviewKeyDown -= OnKey;
                key.LostKeyboardFocus -= OnLost;
                _cancelRecording = null;
                _hotkeys.Suspend(false);
            }

            key.PreviewKeyDown += OnKey;
            key.Focus();
            key.LostKeyboardFocus += OnLost;
            _cancelRecording = Stop;
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(key);
        right.Children.Add(reset);

        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, Style = St("RowTitle") });
        hint.Margin = new Thickness(0, 3, 0, 0);
        left.Children.Add(hint);
        row.Children.Add(left);
        return Padded(row);
    }

    private static string Pretty(string hotkey) => hotkey.Replace("+", " + ");

    // ---- Folder ----

    private static void OpenFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private void ChangeFolder(TextBlock problemText)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Новое место для папки Wipriss" };
        if (dialog.ShowDialog(this) != true) return;

        // A "Wipriss" folder inside the chosen one, unless the user picked an empty folder for it.
        string chosen = dialog.FolderName;
        string target = System.IO.Directory.Exists(chosen) && System.IO.Directory.EnumerateFileSystemEntries(chosen).Any()
            ? System.IO.Path.Combine(chosen, "Wipriss")
            : chosen;

        if (DataFolder.Check(target) is { } problem)
        {
            problemText.Text = problem;
            problemText.Visibility = Visibility.Visible;
            return;
        }

        var answer = MessageBox.Show(this,
            $"Перенести папку Wipriss сюда?\n\n{target}\n\nWipriss перезапустится.",
            "Wipriss", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) MoveFolderRequested?.Invoke(target);
    }

    // ---- Building blocks ----

    private void Header(string title, string? subtitle)
    {
        Page.Children.Add(new TextBlock { Text = title, FontFamily = Font("DisplayFont"), FontSize = 22, FontWeight = FontWeights.SemiBold });
        if (subtitle is not null)
        {
            var sub = Caption(subtitle, "SubtleTextBrush");
            sub.FontSize = 12.5;
            sub.Margin = new Thickness(0, 4, 0, 0);
            Page.Children.Add(sub);
        }
    }

    private void Section(string? title, StackPanel card, string? caption = null)
    {
        if (title is not null)
            Page.Children.Add(new TextBlock { Text = title, Style = St("SectionTitle"), Margin = new Thickness(2, 24, 0, 8) });
        else
            card.Margin = new Thickness(0, 20, 0, 0);
        Page.Children.Add(new Border { Style = St("Card"), Padding = new Thickness(16, 4, 16, 4), Child = card, Margin = card.Margin });
        card.Margin = default;
        if (caption is not null)
        {
            var c = Caption(caption, "FaintTextBrush");
            c.Margin = new Thickness(2, 8, 0, 0);
            Page.Children.Add(c);
        }
    }

    private static StackPanel Card() => new();

    private FrameworkElement ModuleToggle(ModuleInfo info, string? title = null) =>
        Toggle(title ?? info.Title, title is null ? info.Description : null, _store.Current.IsModuleEnabled(info.Id), on =>
        {
            _store.Update(s => s.Modules[info.Id] = on);
            // The page may show more or less once the module runs or stops.
            if (info.Place != ModulePlace.Dock && info.Place != ModulePlace.Card) Dispatcher.BeginInvoke(() => Show(_current));
        });

    private CheckBox Toggle(string title, string? hint, bool value, Action<bool> changed)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = St("RowTitle") });
        if (hint is not null)
        {
            var h = Caption(hint, "SubtleTextBrush");
            h.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(h);
        }
        var box = new CheckBox { Style = St("ToggleSwitch"), Content = text, IsChecked = value, Margin = new Thickness(0, 10, 0, 10) };
        // Subscribed after the value is set, so building the page saves nothing.
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    private Border Divider() => new() { Height = 1, Background = Brush("DividerBrush") };

    private TextBlock Caption(string text, string brush) => new() { Text = text, Style = St("Caption"), Foreground = Brush(brush) };

    private Button Button(string text, string style, Action click)
    {
        var b = new Button { Content = text, Style = St(style) };
        b.Click += (_, _) => click();
        return b;
    }

    private static FrameworkElement Padded(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 12, 0, 12);
        return e;
    }

    private static T WithMargin<T>(T e, Thickness m) where T : FrameworkElement
    {
        e.Margin = m;
        return e;
    }

    private Style St(string key) => (Style)FindResource(key);
    private Brush Brush(string key) => (Brush)FindResource(key);
    private FontFamily Font(string key) => (FontFamily)FindResource(key);

    /// <summary>A module page shown inside Settings rather than under the island.</summary>
    private sealed class PageContext : IPanelContext
    {
        public void Close() { }
        public IDisposable KeepOpen() => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
