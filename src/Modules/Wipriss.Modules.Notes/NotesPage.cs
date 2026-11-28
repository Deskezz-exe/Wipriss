using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wipriss.Core.Modules;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Notes;

/// <summary>
/// Notes inside the open island, under the clipboard. The island never takes the keyboard by itself, so a click into
/// a note asks for it (<see cref="IslandKeyboard"/>) and gives it back once the focus leaves the notes. Saved shortly
/// after typing stops and when the page goes away; empty notes are dropped then.
/// </summary>
internal sealed class NotesPage : Border
{
    private readonly NotesModule _module;
    private readonly List<Note> _notes;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty;
    private readonly DispatcherTimer _saveDelay;
    private IDisposable? _keyboard;

    public NotesPage(NotesModule module)
    {
        _module = module;
        _notes = [.. module.Data.Notes.OrderByDescending(n => n.Updated)];
        Background = HomeLook.CardFill;
        BorderBrush = HomeLook.CardEdge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(HomeLook.CardRadius);
        Padding = new Thickness(16, 12, 12, 12);

        var root = new DockPanel();
        Child = root;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var plus = HomeLook.Glyph("", 10, PillLook.Subtle);
        plus.Margin = new Thickness(0, 1, 7, 0);
        addRow.Children.Add(plus);
        addRow.Children.Add(HomeLook.Text("Новая", 12.5, PillLook.Subtle));
        var add = HomeLook.Button(addRow, 92, 28, AddNote, "Новая заметка", fill: HomeLook.Raised, radius: 10);
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);

        var icon = HomeLook.Glyph("", 13, PillLook.Amber);
        icon.Margin = new Thickness(0, 1, 9, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(HomeLook.Text("Заметки", 13.5, bold: true));

        // The list fills the card (the island gives the tab the home screen's height); more notes scroll inside.
        var list = new Grid();
        list.Children.Add(_scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });
        _empty = HomeLook.Text("Пока пусто. Нажмите «Новая».", 13, PillLook.Subtle);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        list.Children.Add(_empty);
        root.Children.Add(list);

        // Save shortly after typing stops, not on every key press.
        _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDelay.Tick += (_, _) => { _saveDelay.Stop(); Save(); };

        // The keyboard goes back as soon as the focus leaves the notes (another app, a click elsewhere).
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false) ReleaseKeyboard();
        };
        // Another window came forward: nothing is typed here any more.
        EventHandler deactivated = (_, _) => ReleaseKeyboard();
        Window? window = null;
        Loaded += (_, _) =>
        {
            window = Window.GetWindow(this);
            if (window is not null) window.Deactivated += deactivated;
        };
        Unloaded += (_, _) =>
        {
            if (window is not null) window.Deactivated -= deactivated;
            _saveDelay.Stop();
            ReleaseKeyboard();
            _notes.RemoveAll(n => string.IsNullOrWhiteSpace(n.Text));
            Save();
        };
        Fill();
    }

    private void Fill(Note? focus = null)
    {
        _rows.Children.Clear();
        for (int i = 0; i < _notes.Count; i++)
        {
            var row = Row(_notes[i], _notes[i] == focus);
            if (i > 0) row.Margin = new Thickness(0, 6, 4, 0);
            _rows.Children.Add(row);
        }
        _empty.Visibility = _notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A note: the text (grows with its lines) and a bin that asks once more when the note has text.</summary>
    private Border Row(Note note, bool focus)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = HomeLook.Raised,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(6, 4, 6, 4),
        };
        var dock = new DockPanel();
        row.Child = dock;

        var binGlyph = HomeLook.Glyph("", 12, PillLook.Faint);
        bool armed = false;
        var bin = HomeLook.Button(binGlyph, 28, 28, () =>
        {
            // A note with text needs a second click, so one stray click never loses it.
            if (!string.IsNullOrWhiteSpace(note.Text) && !armed)
            {
                armed = true;
                binGlyph.Text = "";
                binGlyph.Foreground = PillLook.Red;
                return;
            }
            _notes.Remove(note);
            Save();
            Fill();
        }, "Удалить", radius: 9);
        bin.VerticalAlignment = VerticalAlignment.Top;
        bin.MouseLeave += (_, _) =>
        {
            armed = false;
            binGlyph.Text = "";
            binGlyph.Foreground = PillLook.Faint;
        };
        DockPanel.SetDock(bin, Dock.Right);
        dock.Children.Add(bin);

        var box = new TextBox
        {
            Text = note.Text,
            FontSize = 13.5,
            Foreground = PillLook.Text,
            CaretBrush = PillLook.Text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 5, 6, 5),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            AcceptsTab = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Resources[SystemColors.HighlightBrushKey] = PillLook.Blue;

        // A hint in an empty note.
        var hint = HomeLook.Text("Текст заметки…", 13.5, PillLook.Faint);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Top;
        hint.Margin = new Thickness(8, 5, 0, 0);
        hint.Visibility = note.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (box.Text == note.Text) return;
            note.Text = box.Text;
            note.Updated = DateTime.Now;
            _saveDelay.Stop();
            _saveDelay.Start();
        };
        // The island does not take the keyboard on a click by itself: ask for it, then put the caret here.
        box.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_keyboard is not null) return;
            TakeKeyboard();
            Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); }, DispatcherPriority.Input);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Keyboard.ClearFocus();
                ReleaseKeyboard();
            }
        };

        var field = new Grid();
        field.Children.Add(box);
        field.Children.Add(hint);
        dock.Children.Add(field);

        if (focus)
            box.Loaded += (_, _) => Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); }, DispatcherPriority.Input);
        return row;
    }

    private void AddNote()
    {
        var note = new Note();
        _notes.Insert(0, note);
        Fill(focus: note); // drops the old fields first: a focused one gives the keyboard back
        TakeKeyboard();
        _scroll.ScrollToTop();
    }

    private void TakeKeyboard() => _keyboard ??= IslandKeyboard.Take();

    private void ReleaseKeyboard()
    {
        _keyboard?.Dispose();
        _keyboard = null;
    }

    private void Save()
    {
        _module.Data.Notes = [.. _notes.Where(n => !string.IsNullOrWhiteSpace(n.Text))];
        _module.Save();
    }
}
