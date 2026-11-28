using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Storage;
using Wipriss.Core.Text;

namespace Wipriss.Modules.ClipboardHistory;

public enum ClipKind { Text, Image }

public sealed class ClipEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    public string Preview { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime Copied { get; set; } = DateTime.Now;
    public string Hash { get; set; } = "";

    /// <summary>Pinned: stays at the top, is never pushed out by new copies and survives "Очистить".</summary>
    public bool Pinned { get; set; }
}

public sealed class ClipIndex
{
    public List<ClipEntry> Items { get; set; } = [];
    public bool Paused { get; set; }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(ClipIndex))]
internal sealed partial class ClipJson : JsonSerializerContext;

/// <summary>
/// Clipboard history. Windows notifies us of every copy (AddClipboardFormatListener), so nothing is polled.
/// The index keeps only short previews in memory; full texts and images are separate encrypted files.
/// </summary>
public sealed class ClipboardModule : IModule, IIslandPage
{
    public const int MaxItems = 20;
    private const int MaxTextChars = 1_000_000;
    private const int MaxImageBytes = 40 * 1024 * 1024;
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private static readonly string Dir = Path.Combine(AppPaths.DataDir, "clipboard");
    private static readonly string IndexPath = Path.Combine(Dir, "index.dat");
    private static readonly string ItemsDir = Path.Combine(Dir, "items");

    private ClipIndex _index = new();
    private HwndSource? _window;
    private IModuleHost? _host;
    private uint _ownSequence;
    private uint _lastSequence;
    private int _attempt;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("clipboard");

    public Size PanelSize => new(440, 600);

    public string Summary =>
        _index.Paused ? "на паузе"
        : _index.Items.Count == 0 ? "пусто"
        : RuText.Count(_index.Items.Count, "запись", "записи", "записей");

    public IReadOnlyList<ClipEntry> Items => _index.Items;

    /// <summary>As shown: the pinned ones first, then the rest, newest first.</summary>
    public IReadOnlyList<ClipEntry> Ordered => [.. _index.Items.Where(e => e.Pinned), .. _index.Items.Where(e => !e.Pinned)];

    public string PageId => "clipboard";

    public FrameworkElement CreatePage() => new ClipboardPage(this);

    /// <summary>Raised on the UI thread when the history changes.</summary>
    public event Action? Changed;

    public bool Paused
    {
        get => _index.Paused;
        set
        {
            _index.Paused = value;
            SaveIndex();
            Changed?.Invoke();
        }
    }

    public void Start(IModuleHost host)
    {
        _host = host;
        _index = SecureStore.ReadJson(IndexPath, ClipJson.Default.ClipIndex) ?? new ClipIndex();
        RemoveOrphanFiles();

        // Message-only window: it gets clipboard notifications and nothing else.
        _window = new HwndSource(new HwndSourceParameters("Wipriss.Clipboard")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        AddClipboardFormatListener(_window.Handle);
    }

    public void Stop()
    {
        if (_window is null) return;
        RemoveClipboardFormatListener(_window.Handle);
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _window = null;
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new ClipboardPanel(this, panel);

    // ---------- capture ----------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            if (!_index.Paused)
            {
                _attempt = 0;
                _host?.Dispatcher.BeginInvoke(Capture, DispatcherPriority.Background);
            }
        }

        return IntPtr.Zero;
    }

    private void Capture()
    {
        if (_window is null || _index.Paused) return;
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == _ownSequence || sequence == _lastSequence) return;

        try
        {
            if (IsPrivate(_window.Handle))
            {
                _lastSequence = sequence;
                return;
            }

            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null) return;

            string? text = data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)
                ? data.GetData(DataFormats.UnicodeText, autoConvert: true) as string
                : null;
            BitmapSource? image = text is null ? ReadImage(data) : null;
            _lastSequence = sequence;

            if (text is not null) AddText(text);
            else if (image is not null) AddImage(image);
        }
        catch (COMException) when (_attempt < 4)
        {
            // Another app is still holding the clipboard: try again shortly.
            _attempt++;
            var retry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40 * _attempt) };
            retry.Tick += (_, _) => { retry.Stop(); Capture(); };
            retry.Start();
        }
        catch (Exception ex) when (ex is ExternalException or IOException or NotSupportedException
                                       or ArgumentException or InvalidOperationException or FileFormatException)
        {
            // This copy can't be read; skip it, but leave a trace (no clipboard content is logged).
            ErrorLog.Write(ex);
        }
    }

    /// <summary>
    /// Password managers and other apps mark secrets so that clipboard history skips them.
    /// We honour every marker Windows defines.
    /// </summary>
    private static bool IsPrivate(IntPtr hwnd)
    {
        if (HasFormat("ExcludeClipboardContentFromMonitorProcessing") || HasFormat("Clipboard Viewer Ignore"))
            return true;

        uint format = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        if (!IsClipboardFormatAvailable(format)) return false;
        if (!OpenClipboard(hwnd)) return true; // can't check the marker: treat as private

        try
        {
            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return false;
            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return false;
            try { return Marshal.ReadInt32(pointer) == 0; }
            finally { GlobalUnlock(handle); }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool HasFormat(string name) => IsClipboardFormatAvailable(RegisterClipboardFormat(name));

    private static BitmapSource? ReadImage(System.Windows.IDataObject data)
    {
        // PNG keeps transparency (browsers, Office); DIB is what screenshots and most apps provide.
        if (data.GetDataPresent("PNG", false) && data.GetData("PNG", false) is MemoryStream png)
            return Decode(png);
        if (data.GetDataPresent(DataFormats.Dib, false) && data.GetData(DataFormats.Dib, false) is MemoryStream dib)
            return DecodeDib(dib.ToArray());
        return null;
    }

    private static BitmapSource Decode(Stream stream)
    {
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

        // A decoded frame stays tied to this thread through its decoder. Copy the pixels into a
        // stand-alone bitmap so it can be encoded to PNG in the background.
        int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, frame.Format, frame.Palette, pixels, stride);
        copy.Freeze();
        return copy;
    }

    /// <summary>A clipboard DIB is a .bmp file without its 14-byte header: add the header and decode.</summary>
    private static BitmapSource? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int colorsUsed = BitConverter.ToInt32(dib, 32);
        int palette = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        int masks = compression == 3 && headerSize == 40 ? 12 : 0; // BI_BITFIELDS
        int pixelOffset = 14 + headerSize + masks + palette * 4;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), pixelOffset);
        dib.CopyTo(file, 14);
        return Decode(new MemoryStream(file));
    }

    private void AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxTextChars) text = text[..MaxTextChars];
        var bytes = Encoding.UTF8.GetBytes(text);
        var preview = text.Trim();
        if (preview.Length > 400) preview = preview[..400];
        AddEntry(new ClipEntry { Kind = ClipKind.Text, Preview = preview, Hash = HashOf(bytes) }, bytes);
    }

    private void AddImage(BitmapSource image)
    {
        // PNG encoding of a big screenshot takes a moment: do it off the UI thread.
        Task.Run(() => EncodePng(image)).ContinueWith(task =>
        {
            if (task.Exception is { } error)
            {
                ErrorLog.Write(error);
                return;
            }
            if (task.Result.Length > MaxImageBytes) return;
            var png = task.Result;
            AddEntry(new ClipEntry
            {
                Kind = ClipKind.Image,
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                Preview = $"{image.PixelWidth}×{image.PixelHeight}",
                Hash = HashOf(png),
            }, png);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void AddEntry(ClipEntry entry, byte[] content)
    {
        int existing = _index.Items.FindIndex(e => e.Hash == entry.Hash);
        if (existing >= 0 && _index.Items[existing].Pinned)
        {
            return; // already kept, pinned in its place
        }
        if (existing >= 0)
        {
            // Same content copied again: move it to the top instead of storing a duplicate.
            var old = _index.Items[existing];
            _index.Items.RemoveAt(existing);
            old.Copied = DateTime.Now;
            _index.Items.Insert(0, old);
        }
        else
        {
            SecureStore.WriteBytes(ItemPath(entry.Id), content);
            _index.Items.Insert(0, entry);
        }

        // Only the unpinned ones count towards the limit; the oldest of them goes.
        while (_index.Items.Count(e => !e.Pinned) > MaxItems)
        {
            var oldest = _index.Items.Last(e => !e.Pinned);
            _index.Items.Remove(oldest);
            TryDelete(ItemPath(oldest.Id));
        }

        SaveIndex();
        Changed?.Invoke();
    }

    // ---------- actions from the panel ----------

    /// <summary>Decrypted content of an entry, or null if its file is gone.</summary>
    public byte[]? ReadContent(ClipEntry entry)
    {
        try { return SecureStore.ReadBytes(ItemPath(entry.Id)); }
        catch (Exception ex) when (ex is CryptographicException or IOException) { return null; }
    }

    /// <summary>
    /// Puts an entry back on the clipboard. The history stays as it is: our own copy is not recorded again and the
    /// entry keeps its place.
    /// </summary>
    public bool CopyBack(ClipEntry entry)
    {
        var bytes = ReadContent(entry);
        if (bytes is null) return false;

        var data = new DataObject();
        if (entry.Kind == ClipKind.Text)
        {
            data.SetText(Encoding.UTF8.GetString(bytes), TextDataFormat.UnicodeText);
        }
        else
        {
            data.SetImage(Decode(new MemoryStream(bytes)));
            data.SetData("PNG", new MemoryStream(bytes));
        }

        try { System.Windows.Clipboard.SetDataObject(data, copy: true); }
        catch (ExternalException) { return false; }
        _ownSequence = GetClipboardSequenceNumber(); // don't record our own copy again
        return true;
    }

    public void SetPinned(ClipEntry entry, bool pinned)
    {
        if (entry.Pinned == pinned) return;
        entry.Pinned = pinned;
        // Unpinned, it goes back among the others by when it was copied.
        if (!pinned)
        {
            _index.Items.Remove(entry);
            int at = _index.Items.FindIndex(e => !e.Pinned && e.Copied < entry.Copied);
            _index.Items.Insert(at < 0 ? _index.Items.Count : at, entry);
        }
        SaveIndex();
        Changed?.Invoke();
    }

    public void Delete(ClipEntry entry)
    {
        _index.Items.Remove(entry);
        TryDelete(ItemPath(entry.Id));
        SaveIndex();
        Changed?.Invoke();
    }

    /// <summary>Removes everything except the pinned entries.</summary>
    public void Clear()
    {
        foreach (var entry in _index.Items.Where(e => !e.Pinned)) TryDelete(ItemPath(entry.Id));
        _index.Items.RemoveAll(e => !e.Pinned);
        SaveIndex();
        Changed?.Invoke();
    }

    // ---------- storage ----------

    private void SaveIndex() => SecureStore.WriteJson(IndexPath, _index, ClipJson.Default.ClipIndex);

    private static string ItemPath(Guid id) => Path.Combine(ItemsDir, id.ToString("N") + ".dat");

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..32];

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private void RemoveOrphanFiles()
    {
        if (!Directory.Exists(ItemsDir)) return;
        var known = _index.Items.Select(e => e.Id.ToString("N")).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(ItemsDir))
        {
            if (!known.Contains(Path.GetFileNameWithoutExtension(file))) TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------- Win32 ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);
}
