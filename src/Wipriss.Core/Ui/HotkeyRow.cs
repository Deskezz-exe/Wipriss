using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wipriss.Core.Input;

namespace Wipriss.Core.Ui;

/// <summary>
/// A settings row that records a key combination: the title and a hint on the left; the key, "Отмена" while recording
/// and "По умолчанию" on the right. Recording ends with a key, "Отмена", Esc or a click elsewhere; anything but a key
/// keeps the old combination.
/// </summary>
public sealed class HotkeyRow : DockPanel
{
    private readonly Button _key, _cancel, _reset;
    private readonly TextBlock _hint;
    private readonly Hotkey _default;
    private readonly Func<Hotkey, bool> _allowed;
    private readonly string _rule;
    private readonly string _idleHint;
    private Hotkey _current;
    private bool _recording;

    /// <param name="allowed">Whether a combination may be used (e.g. <see cref="Hotkey.IsUsable"/>).</param>
    /// <param name="rule">Shown when a combination is not allowed, e.g. "Добавьте Ctrl, Alt или Win".</param>
    /// <param name="problem">Shown in amber under the title (e.g. the key is taken), or null.</param>
    public HotkeyRow(string title, Hotkey current, Hotkey defaultKey, Func<Hotkey, bool> allowed, string rule, string? problem = null)
    {
        _current = current;
        _default = defaultKey;
        _allowed = allowed;
        _rule = rule;
        _idleHint = "Нажмите на клавишу справа, затем новое сочетание.";

        _key = new Button { Style = Res<Style>("PlainButton"), MinWidth = 130, Focusable = true };
        _cancel = new Button { Style = Res<Style>("PlainButton"), Content = "Отмена", Focusable = false, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
        _reset = new Button { Style = Res<Style>("PlainButton"), Content = "По умолчанию", Focusable = false, Margin = new Thickness(6, 0, 0, 0) };
        _key.Click += (_, _) => Begin();
        _cancel.Click += (_, _) => Cancel();
        _reset.Click += (_, _) => Set(_default);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_key);
        right.Children.Add(_cancel);
        right.Children.Add(_reset);
        SetDock(right, Dock.Right);
        Children.Add(right);

        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, Style = Res<Style>("RowTitle") });
        _hint = new TextBlock { Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        left.Children.Add(_hint);
        Children.Add(left);

        Margin = new Thickness(16, 12, 16, 12);
        ShowIdle(problem);
        Unloaded += (_, _) => { if (_recording) Cancel(); };
    }

    /// <summary>A new combination was chosen (already allowed). Empty means "no key".</summary>
    public event Action<Hotkey>? Chosen;

    /// <summary>Recording started (true) or ended (false): a module may pause its own key hook meanwhile.</summary>
    public event Action<bool>? Recording;

    /// <summary>Lets Backspace/Delete clear the key.</summary>
    public bool AllowNone { get; init; } = true;

    private void ShowIdle(string? problem = null)
    {
        _key.Content = _current.IsEmpty ? "Нет" : Pretty(_current);
        _cancel.Visibility = Visibility.Collapsed;
        _reset.Visibility = _current == _default ? Visibility.Collapsed : Visibility.Visible;
        _hint.Text = problem ?? _idleHint;
        _hint.Foreground = Res<Brush>(problem is null ? "SubtleTextBrush" : "WarnBrush");
    }

    private void Begin()
    {
        if (_recording) return;
        _recording = true;
        _key.Content = "Нажмите клавиши…";
        _cancel.Visibility = Visibility.Visible;
        _reset.Visibility = Visibility.Collapsed;
        _hint.Text = AllowNone ? "Esc или «Отмена» — оставить как было. Backspace — без клавиши." : "Esc или «Отмена» — оставить как было.";
        _hint.Foreground = Res<Brush>("SubtleTextBrush");
        _key.PreviewKeyDown += OnKey;
        _key.PreviewKeyUp += OnKeyUp;
        _key.LostKeyboardFocus += OnLost;
        _key.Focus();
        Recording?.Invoke(true);
    }

    private void End()
    {
        if (!_recording) return;
        _recording = false;
        _key.PreviewKeyDown -= OnKey;
        _key.PreviewKeyUp -= OnKeyUp;
        _key.LostKeyboardFocus -= OnLost;
        Recording?.Invoke(false);
    }

    /// <summary>Stops recording and keeps the old combination.</summary>
    public void Cancel()
    {
        End();
        ShowIdle();
    }

    private void Set(Hotkey key)
    {
        End();
        _current = key;
        ShowIdle();
        Chosen?.Invoke(key);
    }

    private void OnLost(object sender, RoutedEventArgs e) => Cancel();

    // PrtSc never sends a key-down to windows, only a key-up.
    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k != Key.Snapshot) return;
        e.Handled = true;
        Take(k);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Snapshot)
            return;
        if (k == Key.Escape) { Cancel(); return; }
        if (k is Key.Back or Key.Delete && AllowNone) { Set(default); return; }
        Take(k);
    }

    private void Take(Key k)
    {
        var mods = Keyboard.Modifiers;
        bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        int flags = (mods.HasFlag(ModifierKeys.Control) ? Hotkey.Ctrl : 0) | (mods.HasFlag(ModifierKeys.Alt) ? Hotkey.Alt : 0)
                    | (mods.HasFlag(ModifierKeys.Shift) ? Hotkey.Shift : 0) | (win ? Hotkey.Win : 0);
        Offer(new Hotkey(flags, KeyInterop.VirtualKeyFromKey(k)));
    }

    /// <summary>A combination caught elsewhere (a module's own key hook) while recording.</summary>
    public void Offer(Hotkey hk)
    {
        if (!_recording) return;
        if (!_allowed(hk))
        {
            _hint.Text = _rule;
            _hint.Foreground = Res<Brush>("WarnBrush");
            return;
        }
        Set(hk);
    }

    public static string Pretty(Hotkey key) => key.ToString().Replace("+", " + ");

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}
