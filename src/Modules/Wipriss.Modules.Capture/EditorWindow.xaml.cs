using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Wipriss.Core;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Capture;

/// <summary>
/// Draw on a screenshot, hide private parts, crop, then copy or save. Nothing touches the disk until "Сохранить":
/// closing the window simply drops the picture.
/// </summary>
public partial class EditorWindow : Window
{
    private const int UndoLimit = 20;

    private readonly Stack<EditorState> _undo = new();
    private readonly Stack<EditorState> _redo = new();
    private readonly List<ShapeData> _shapes = [];
    private BitmapSource _base;
    private EditorState _current;
    private Color _color = Color.FromRgb(0xE5, 0x48, 0x4D);
    private double _width = 6;
    private double _exportScale = 1;
    private Point? _dragStart;
    private Shape? _preview;
    private string? _savedPath;

    internal EditorWindow(BitmapSource image)
    {
        InitializeComponent();
        _base = image;
        BaseImage.Source = image;
        Ink.StrokeCollected += (_, _) => Changed();
        _current = Snapshot();

        // Big enough for the picture, at most 85 % of the screen.
        var work = SystemParameters.WorkArea;
        Width = Math.Clamp(image.PixelWidth + 80, MinWidth, work.Width * 0.85);
        Height = Math.Clamp(image.PixelHeight + 160, MinHeight, work.Height * 0.85);

        Viewer.SizeChanged += (_, _) => FitToWindow();
        ApplyTool();
        UpdateInfo();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.DarkTitleBar(this);
        NativeWindowStyle.FrameColors(this, (Color)FindResource("HairlineColor"), (Color)FindResource("WindowColor"), (Color)FindResource("TextColor"));
    }

    protected override void OnClosed(EventArgs e)
    {
        // Drop the picture and its history: nothing of it stays in memory or on disk.
        _undo.Clear();
        _redo.Clear();
        BaseImage.Source = null;
        Ink.Strokes.Clear();
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (ctrl && e.Key == Key.C) { Copy_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { Save_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && !shift) { Undo_Click(this, e); e.Handled = true; }
        else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { Redo_Click(this, e); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ---- Tools ----

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded || Ink is not null) ApplyTool();
    }

    private void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is Control { Background: SolidColorBrush brush }) _color = brush.Color;
        ApplyTool();
    }

    private void Width_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) _width = double.Parse(tag, CultureInfo.InvariantCulture);
        ApplyTool();
    }

    private void Scale_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) _exportScale = double.Parse(tag, CultureInfo.InvariantCulture);
        UpdateInfo();
    }

    /// <summary>Pen and marker draw straight on the ink layer; the other tools work by dragging a rectangle.</summary>
    private void ApplyTool()
    {
        if (Ink is null || Draft is null) return;
        bool pen = PenTool.IsChecked == true, marker = MarkerTool.IsChecked == true;
        Ink.EditingMode = pen || marker ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        Draft.Visibility = pen || marker ? Visibility.Collapsed : Visibility.Visible;
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = marker ? Color.FromArgb(255, _color.R, _color.G, _color.B) : _color,
            Width = marker ? _width * 3.5 : _width,
            Height = marker ? _width * 3.5 : _width,
            IsHighlighter = marker,
            StylusTip = marker ? StylusTip.Rectangle : StylusTip.Ellipse,
            FitToCurve = true,
            IgnorePressure = true,
        };
    }

    private void Draft_Down(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Draft);
        Draft.CaptureMouse();
        _preview = BlurTool.IsChecked == true || CropTool.IsChecked == true
            ? new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0xE9, 0xE5, 0xDD)), StrokeThickness = 1.5, StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) }
            : new Path { Stroke = new SolidColorBrush(_color), StrokeThickness = _width, Fill = new SolidColorBrush(_color), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        Draft.Children.Add(_preview);
    }

    private void Draft_Move(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _preview is null) return;
        var point = Clamp(e.GetPosition(Draft));
        if (_preview is Rectangle box)
        {
            var r = new Rect(start, point);
            Canvas.SetLeft(box, r.X);
            Canvas.SetTop(box, r.Y);
            box.Width = r.Width;
            box.Height = r.Height;
        }
        else if (_preview is Path path)
        {
            var data = new ShapeData(ArrowTool.IsChecked == true ? ShapeKind.Arrow : ShapeKind.Box, start, point, _color, _width);
            path.Data = data.Geometry();
            path.Fill = data.Kind == ShapeKind.Arrow ? new SolidColorBrush(_color) : null;
        }
    }

    private void Draft_Up(object sender, MouseButtonEventArgs e)
    {
        Draft.ReleaseMouseCapture();
        Draft.Children.Clear();
        if (_dragStart is not { } start) return;
        _dragStart = null;
        _preview = null;

        var end = Clamp(e.GetPosition(Draft));
        var area = new Rect(start, end);
        if (area.Width < 3 && area.Height < 3) return;

        if (ArrowTool.IsChecked == true || BoxTool.IsChecked == true)
        {
            _shapes.Add(new ShapeData(ArrowTool.IsChecked == true ? ShapeKind.Arrow : ShapeKind.Box, start, end, _color, _width));
            RedrawShapes();
            Changed();
        }
        else if (BlurTool.IsChecked == true)
        {
            SetBase(Pixelate(_base, ToPixels(area)));
            Changed();
        }
        else if (CropTool.IsChecked == true)
        {
            var cropped = new CroppedBitmap(Compose(1), ToPixels(area));
            cropped.Freeze();
            Ink.Strokes.Clear();
            _shapes.Clear();
            RedrawShapes();
            SetBase(Freeze(cropped));
            Changed();
        }
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _base.PixelWidth), Math.Clamp(p.Y, 0, _base.PixelHeight));

    private Int32Rect ToPixels(Rect r)
    {
        int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y);
        int right = Math.Min(_base.PixelWidth, (int)Math.Ceiling(r.Right)), bottom = Math.Min(_base.PixelHeight, (int)Math.Ceiling(r.Bottom));
        return new Int32Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    // ---- Undo ----

    private void Changed()
    {
        _undo.Push(_current);
        if (_undo.Count > UndoLimit) TrimUndo();
        _redo.Clear();
        _current = Snapshot();
        _savedPath = null;
        UpdateInfo();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Push(_current);
        Restore(_undo.Pop());
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(_current);
        Restore(_redo.Pop());
    }

    private EditorState Snapshot() => new(_base, Ink.Strokes.Clone(), [.. _shapes]);

    private void Restore(EditorState state)
    {
        _current = state;
        if (!ReferenceEquals(state.Base, _base)) SetBase(state.Base);
        Ink.Strokes = state.Strokes.Clone();
        _shapes.Clear();
        _shapes.AddRange(state.Shapes);
        RedrawShapes();
        UpdateInfo();
    }

    private void TrimUndo()
    {
        var keep = _undo.Take(UndoLimit).Reverse().ToList();
        _undo.Clear();
        foreach (var state in keep) _undo.Push(state);
    }

    // ---- Picture ----

    private void SetBase(BitmapSource image)
    {
        _base = image;
        BaseImage.Source = image;
        FitToWindow();
        UpdateInfo();
    }

    private void RedrawShapes()
    {
        Shapes.Children.Clear();
        foreach (var shape in _shapes)
        {
            Shapes.Children.Add(new Path
            {
                Data = shape.Geometry(),
                Stroke = new SolidColorBrush(shape.Color),
                StrokeThickness = shape.Width,
                Fill = shape.Kind == ShapeKind.Arrow ? new SolidColorBrush(shape.Color) : null,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
    }

    /// <summary>Whole picture fits; very tall ones (scrolling captures) fit by width and scroll.</summary>
    private void FitToWindow()
    {
        double width = Math.Max(100, Viewer.ActualWidth - 64), height = Math.Max(100, Viewer.ActualHeight - 64);
        bool tall = _base.PixelHeight > _base.PixelWidth * 2.5;
        double scale = Math.Min(1, width / _base.PixelWidth);
        if (!tall) scale = Math.Min(scale, height / _base.PixelHeight);
        Zoom.ScaleX = Zoom.ScaleY = scale;
        Ink.Width = Shapes.Width = Draft.Width = _base.PixelWidth;
        Ink.Height = Shapes.Height = Draft.Height = _base.PixelHeight;
    }

    /// <summary>Picture plus drawings, at the chosen size.</summary>
    private BitmapSource Compose(double scale)
    {
        int width = Math.Max(1, (int)Math.Round(_base.PixelWidth * scale)), height = Math.Max(1, (int)Math.Round(_base.PixelHeight * scale));
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawImage(_base, new Rect(0, 0, _base.PixelWidth, _base.PixelHeight));
            foreach (var shape in _shapes)
            {
                var pen = new Pen(new SolidColorBrush(shape.Color), shape.Width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawGeometry(shape.Kind == ShapeKind.Arrow ? new SolidColorBrush(shape.Color) : null, pen, shape.Geometry());
            }

            Ink.Strokes.Draw(dc);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Big blocks of averaged colour: the hidden text can't be read back.</summary>
    private static BitmapSource Pixelate(BitmapSource image, Int32Rect area)
    {
        var (pixels, width, height) = Images.Read(image);
        int block = Math.Max(10, Math.Min(area.Width, area.Height) / 6);
        for (int by = area.Y; by < area.Y + area.Height; by += block)
            for (int bx = area.X; bx < area.X + area.Width; bx += block)
            {
                int bw = Math.Min(block, area.X + area.Width - bx), bh = Math.Min(block, area.Y + area.Height - by);
                long b = 0, g = 0, r = 0;
                for (int y = by; y < by + bh; y++)
                    for (int x = bx; x < bx + bw; x++)
                    {
                        int i = (y * width + x) * 4;
                        b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2];
                    }

                int n = bw * bh;
                for (int y = by; y < by + bh; y++)
                    for (int x = bx; x < bx + bw; x++)
                    {
                        int i = (y * width + x) * 4;
                        pixels[i] = (byte)(b / n); pixels[i + 1] = (byte)(g / n); pixels[i + 2] = (byte)(r / n); pixels[i + 3] = 255;
                    }
            }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    private static BitmapSource Freeze(BitmapSource image)
    {
        var (pixels, width, height) = Images.Read(image);
        var copy = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        copy.Freeze();
        return copy;
    }

    // ---- Output ----

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        bool ok = ClipboardWriter.Put(Compose(_exportScale));
        ShowStatus(ok ? "Скопировано. Можно вставлять." : "Буфер обмена занят другой программой, попробуйте ещё раз.");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            string name = $"Скриншот {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            string path = System.IO.Path.Combine(AppPaths.ScreenshotsDir, name + ".png");
            for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(AppPaths.ScreenshotsDir, $"{name} ({i}).png");
            File.WriteAllBytes(path, Images.Png(Compose(_exportScale)));
            _savedPath = path;
            UpdateInfo();
            ShowStatus($"Сохранено: {System.IO.Path.GetFileName(path)}");
            ShowFileButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            ShowStatus("Не получилось сохранить: нет доступа к папке Wipriss.");
        }
    }

    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (_savedPath is null || !File.Exists(_savedPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_savedPath}\"") { UseShellExecute = true });
    }

    private void UpdateInfo()
    {
        if (InfoText is null || _base is null) return;
        int width = (int)Math.Round(_base.PixelWidth * _exportScale), height = (int)Math.Round(_base.PixelHeight * _exportScale);
        InfoText.Text = $"{width} × {height} px · " + (_savedPath is null ? "не сохранён: закроете окно, и снимок исчезнет" : "сохранён в папке Wipriss");
        if (_savedPath is null && ShowFileButton is not null) ShowFileButton.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string text) => InfoText.Text = text;
}

internal enum ShapeKind { Arrow, Box }

/// <summary>An arrow or a frame drawn on the picture, kept as data so undo and saving redraw it exactly.</summary>
internal sealed record ShapeData(ShapeKind Kind, Point A, Point B, Color Color, double Width)
{
    public Geometry Geometry()
    {
        if (Kind == ShapeKind.Box) return new RectangleGeometry(new Rect(A, B), 2, 2);

        // A line with a filled head that grows with the line width.
        var direction = B - A;
        double length = direction.Length;
        if (length < 1) return new LineGeometry(A, B);
        direction /= length;
        double head = Math.Min(length * 0.6, 10 + Width * 3);
        var normal = new Vector(-direction.Y, direction.X);
        var baseCenter = B - direction * head;
        var left = baseCenter + normal * head * 0.5;
        var right = baseCenter - normal * head * 0.5;

        var group = new GeometryGroup();
        group.Children.Add(new LineGeometry(A, baseCenter + direction * 1));
        var tip = new StreamGeometry();
        using (var ctx = tip.Open())
        {
            ctx.BeginFigure(B, isFilled: true, isClosed: true);
            ctx.LineTo(left, true, true);
            ctx.LineTo(right, true, true);
        }

        group.Children.Add(tip);
        return group;
    }
}

internal sealed record EditorState(BitmapSource Base, StrokeCollection Strokes, List<ShapeData> Shapes);
