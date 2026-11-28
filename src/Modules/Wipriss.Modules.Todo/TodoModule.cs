using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Storage;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Todo;

/// <summary>
/// A to-do list on the home screen, under the timer: tick a task by clicking it (crossed out), click again to bring it
/// back, the bin removes it, "+ добавить задачу" types a new one. Kept encrypted in the Wipriss folder. Nothing runs
/// while the island is closed.
/// </summary>
public sealed class TodoModule : IModule, IIslandCard
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "todo.dat");

    private TodoList _list = new(new TodoData());

    public ModuleInfo Info { get; } = ModuleCatalog.Get("todo");

    public Size PanelSize => new(0, 0);

    public string Summary => "";

    public HomeArea Area => HomeArea.SideMiddle;

    public bool HasContent => true;

    public event Action? Changed { add { } remove { } }

    public void Start(IModuleHost host)
    {
        try { _list = new TodoList(SecureStore.ReadJson(FilePath, TodoJson.Default.TodoData) ?? new TodoData()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorLog.Write(ex); }
    }

    public void Stop() { }

    public FrameworkElement CreatePanel(IPanelContext panel) => new TodoCard(this);

    public FrameworkElement CreateCard() => new TodoCard(this);

    internal TodoList List => _list;

    internal void Save()
    {
        try { SecureStore.WriteJson(FilePath, _list.Data, TodoJson.Default.TodoData); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorLog.Write(ex); }
    }
}

/// <summary>
/// The card. It redraws only its own rows (the island is not rebuilt), so typing is never interrupted; the island
/// follows its height. Many tasks scroll inside the card.
/// </summary>
internal sealed class TodoCard : Border
{
    private const double TextSize = 13;
    private const double MaxListHeight = 250;

    private readonly TodoModule _module;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _count;
    private readonly Border _add;
    private readonly TextBox _input;
    private IDisposable? _keyboard;

    public TodoCard(TodoModule module)
    {
        _module = module;
        Background = HomeLook.CardFill;
        BorderBrush = HomeLook.CardEdge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(HomeLook.CardRadius);
        Padding = new Thickness(16, 12, 12, 10);

        var root = new StackPanel();
        Child = root;

        var head = new DockPanel { Margin = new Thickness(0, 0, 4, 6) };
        _count = HomeLook.Text("", 11.5, PillLook.Subtle);
        DockPanel.SetDock(_count, Dock.Right);
        head.Children.Add(_count);
        var icon = HomeLook.Glyph("", 13, PillLook.Green);
        icon.Margin = new Thickness(0, 1, 9, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(HomeLook.Text("Задачи", 13.5, bold: true));
        root.Children.Add(head);

        root.Children.Add(new ScrollViewer
        {
            Content = _rows,
            MaxHeight = MaxListHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });

        // The last line: "+ добавить задачу", or the field while typing.
        _input = new TextBox
        {
            FontSize = TextSize,
            Foreground = PillLook.Text,
            CaretBrush = PillLook.Text,
            Background = HomeLook.Raised,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            TextWrapping = TextWrapping.Wrap,
            MaxLength = TodoList.MaxLength,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 4, 0),
        };
        _input.Resources[SystemColors.HighlightBrushKey] = PillLook.Blue;
        _input.PreviewKeyDown += OnInputKey;
        _input.LostKeyboardFocus += (_, _) => EndInput(keep: true);
        root.Children.Add(_input);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        var plus = HomeLook.Glyph("", 11, PillLook.Subtle);
        plus.Margin = new Thickness(2, 1, 10, 0);
        addRow.Children.Add(plus);
        addRow.Children.Add(HomeLook.Text("добавить задачу", TextSize, PillLook.Subtle));
        _add = HomeLook.Button(addRow, double.NaN, 30, BeginInput, radius: 9);
        _add.HorizontalAlignment = HorizontalAlignment.Stretch;
        _add.Padding = new Thickness(4, 0, 8, 0);
        _add.Margin = new Thickness(-4, 2, 4, 0);
        addRow.VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(_add);

        Unloaded += (_, _) => { _keyboard?.Dispose(); _keyboard = null; };
        Fill();
    }

    private void Fill()
    {
        _rows.Children.Clear();
        var items = _module.List.Items;
        for (int i = 0; i < items.Count; i++) _rows.Children.Add(Row(items[i], i));
        _count.Text = items.Count == 0 ? "" : $"{_module.List.DoneCount} из {items.Count}";
    }

    /// <summary>A bin on the left, the text on the right (wraps, fixed size); a click on the text ticks it.</summary>
    private FrameworkElement Row(TodoItem item, int index)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 4, 1) };
        var bin = HomeLook.Button(HomeLook.Glyph("", 12, PillLook.Faint), 26, 26, () =>
        {
            _module.List.Remove(index);
            _module.Save();
            Fill();
        }, "Удалить", radius: 8);
        bin.VerticalAlignment = VerticalAlignment.Top;
        bin.Margin = new Thickness(-4, 1, 6, 0);
        DockPanel.SetDock(bin, Dock.Left);
        row.Children.Add(bin);

        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = TextSize,
            TextWrapping = TextWrapping.Wrap,
            Foreground = item.Done ? PillLook.Faint : PillLook.Text,
            TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
            LineHeight = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hit = new Border
        {
            Child = text, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(8), ToolTip = item.Done ? "Вернуть в работу" : "Отметить выполненной",
        };
        hit.MouseEnter += (_, _) => hit.Background = HomeLook.Hover;
        hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
        hit.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _module.List.Toggle(index);
            _module.Save();
            Fill();
        };
        row.Children.Add(hit);
        return row;
    }

    private void BeginInput()
    {
        if (_input.Visibility == Visibility.Visible) return;
        _keyboard ??= IslandKeyboard.Take();
        _add.Visibility = Visibility.Collapsed;
        _input.Text = "";
        _input.Visibility = Visibility.Visible;
        // The island takes the focus first; the field gets it right after.
        Dispatcher.BeginInvoke(() => { _input.Focus(); Keyboard.Focus(_input); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_module.List.Add(_input.Text))
            {
                _module.Save();
                Fill();
            }
            _input.Text = ""; // ready for the next one
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndInput(keep: false);
        }
    }

    private void EndInput(bool keep)
    {
        if (_input.Visibility != Visibility.Visible) return;
        if (keep && _module.List.Add(_input.Text))
        {
            _module.Save();
            Fill();
        }
        _input.Visibility = Visibility.Collapsed;
        _add.Visibility = Visibility.Visible;
        _keyboard?.Dispose();
        _keyboard = null;
    }
}
