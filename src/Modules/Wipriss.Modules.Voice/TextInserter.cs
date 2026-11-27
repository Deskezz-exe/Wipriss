using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Wipriss.Modules.Voice;

/// <summary>
/// Puts recognized text where the cursor is: the text goes on the clipboard for a moment, Ctrl+V is pressed,
/// then the owner's clipboard comes back. Both our text and the returned content are marked so that neither
/// Windows' clipboard history nor Wipriss' own history records them. Runs on the UI thread.
/// </summary>
internal static class TextInserter
{
    /// <summary>How long the target program gets to read the clipboard after Ctrl+V.</summary>
    private static readonly TimeSpan PasteTime = TimeSpan.FromMilliseconds(450);

    private static readonly int[] Modifiers = [HotkeyKeys.Shift, HotkeyKeys.Ctrl, HotkeyKeys.Alt, HotkeyKeys.Win, HotkeyKeys.RWin];

    /// <returns>False when the clipboard stayed locked by another program.</returns>
    public static async Task<bool> InsertAsync(string text)
    {
        // Ctrl+V with Alt or Win still held would be another shortcut: wait (briefly) until the hotkey is let go.
        for (int i = 0; i < 50 && Modifiers.Any(IsDown); i++) await Task.Delay(30);

        var saved = Snapshot();
        var ours = new DataObject();
        ours.SetText(text, TextDataFormat.UnicodeText);
        MarkPrivate(ours);
        if (!await TrySetAsync(ours)) return false;

        PressCtrlV();
        await Task.Delay(PasteTime);

        // Give the owner's clipboard back, unless something else was copied in the meantime.
        string? now = null;
        try { if (Clipboard.ContainsText()) now = Clipboard.GetText(); } catch (COMException) { }
        if (now == text)
        {
            if (saved is null) { try { Clipboard.Clear(); } catch (COMException) { } }
            else await TrySetAsync(saved);
        }
        return true;
    }

    /// <summary>A copy of what is on the clipboard now: text, files, pictures and plain data formats.</summary>
    private static DataObject? Snapshot()
    {
        IDataObject? current;
        try { current = Clipboard.GetDataObject(); }
        catch (COMException) { return null; }
        if (current is null) return null;

        var copy = new DataObject();
        bool any = false;
        string[] formats;
        try { formats = current.GetFormats(autoConvert: false); }
        catch (COMException) { return null; }
        foreach (var format in formats)
        {
            if (format is "CanIncludeInClipboardHistory" or "ExcludeClipboardContentFromMonitorProcessing") continue;
            object? data;
            try { data = current.GetData(format, autoConvert: false); }
            catch (Exception ex) when (ex is COMException or OutOfMemoryException or ExternalException or InvalidOperationException) { continue; }
            switch (data)
            {
                case string or string[]:
                    copy.SetData(format, data, autoConvert: false);
                    any = true;
                    break;
                case MemoryStream stream:
                    copy.SetData(format, new MemoryStream(stream.ToArray()), autoConvert: false);
                    any = true;
                    break;
                case BitmapSource image:
                    copy.SetData(format, image, autoConvert: false);
                    any = true;
                    break;
                // Other kinds (metafiles, objects of other programs) cannot be kept; they are rare in a clipboard.
            }
        }
        if (!any) return null;
        MarkPrivate(copy);
        return copy;
    }

    private static void MarkPrivate(DataObject data)
    {
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));
    }

    private static async Task<bool> TrySetAsync(DataObject data)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException)
            {
                await Task.Delay(40); // another program holds the clipboard for a moment
            }
        }
        return false;
    }

    private static void PressCtrlV()
    {
        const ushort V = 0x56;
        var inputs = new HotkeyHook.Input[4];
        inputs[0] = Key(HotkeyKeys.Ctrl, up: false);
        inputs[1] = Key(V, up: false);
        inputs[2] = Key(V, up: true);
        inputs[3] = Key(HotkeyKeys.Ctrl, up: true);
        HotkeyHook.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<HotkeyHook.Input>());
    }

    private static HotkeyHook.Input Key(int vk, bool up) =>
        new() { Type = 1, Key = new HotkeyHook.KeyInput { Vk = (ushort)vk, Flags = up ? 2u : 0u } };

    private static bool IsDown(int vk) => (HotkeyHook.GetAsyncKeyState(vk) & 0x8000) != 0;
}
