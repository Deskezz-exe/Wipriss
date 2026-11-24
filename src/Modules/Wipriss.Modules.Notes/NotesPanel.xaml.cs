using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wipriss.Core.Modules;

namespace Wipriss.Modules.Notes;

public partial class NotesPanel : UserControl
{
    private const string TrashGlyph = "";
    private const string ConfirmGlyph = "";

    private readonly NotesModule _module;
    private readonly IPanelContext _panel;
    private readonly ObservableCollection<Note> _items;
    private readonly DispatcherTimer _saveDelay;
    private Note? _focusNext;

    internal NotesPanel(NotesModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        _panel = panel;
        _items = new ObservableCollection<Note>(module.Data.Notes.OrderByDescending(n => n.Updated));
        List.ItemsSource = _items;

        // Save shortly after typing stops, not on every key press.
        _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDelay.Tick += (_, _) => { _saveDelay.Stop(); Save(); };

        if (_items.Count == 0) AddNote();
        UpdateEmpty();
        Unloaded += (_, _) => { _saveDelay.Stop(); RemoveEmpty(); Save(); };
    }

    private void Save()
    {
        _module.Data.Notes = [.. _items];
        _module.Save();
    }

    private void AddNote()
    {
        var note = new Note();
        _items.Insert(0, note);
        _focusNext = note;
        UpdateEmpty();
    }

    private void RemoveEmpty()
    {
        foreach (var note in _items.Where(n => string.IsNullOrWhiteSpace(n.Text)).ToList())
            _items.Remove(note);
    }

    private void UpdateEmpty() => EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private string AllText() =>
        string.Join($"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}",
            _items.Where(n => !string.IsNullOrWhiteSpace(n.Text)).Select(n => n.Text.Trim()));

    private void New_Click(object sender, RoutedEventArgs e) => AddNote();

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var text = AllText();
        if (text.Length == 0) return;
        Clipboard.SetText(text);
        Flash("Все заметки скопированы.");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var text = AllText();
        if (text.Length == 0) return;

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить заметки",
            FileName = $"Заметки {DateTime.Now:yyyy-MM-dd}",
            Filter = "Текст (*.txt)|*.txt|Markdown (*.md)|*.md",
            AddExtension = true,
        };

        using (_panel.KeepOpen())
        {
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        }

        File.WriteAllText(dialog.FileName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Flash("Сохранено.");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Note note } button) return;

        // A note with text needs a second click, so one stray click never loses it.
        if (!string.IsNullOrWhiteSpace(note.Text) && (string)button.Content == TrashGlyph)
        {
            button.Content = ConfirmGlyph;
            button.Foreground = (Brush)FindResource("DangerBrush");
            button.ToolTip = "Нажмите ещё раз, чтобы удалить";
            return;
        }

        _items.Remove(note);
        UpdateEmpty();
        Save();
    }

    private void Delete_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Button button) return;
        button.Content = TrashGlyph;
        button.ClearValue(ForegroundProperty);
        button.ToolTip = "Удалить";
    }

    private void Note_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Ignore the change that comes from loading the text; react only to typing.
        if (sender is not TextBox { DataContext: Note note, IsKeyboardFocused: true }) return;
        note.Updated = DateTime.Now;
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void Note_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: Note note } box && note == _focusNext)
        {
            _focusNext = null;
            box.Focus();
        }
    }

    private void Flash(string message)
    {
        var original = Hint.Text;
        Hint.Text = message;
        var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        reset.Tick += (_, _) => { reset.Stop(); Hint.Text = original; };
        reset.Start();
    }
}
