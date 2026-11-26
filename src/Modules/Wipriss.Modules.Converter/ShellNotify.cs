using System.IO;
using System.Runtime.InteropServices;

namespace Wipriss.Modules.Converter;

/// <summary>
/// Tells Explorer that a file appeared, so the desktop and open folders show it at once. Files written by another
/// process (the converter) or moved after recycling the original are otherwise noticed by the desktop only on refresh.
/// </summary>
internal static class ShellNotify
{
    private const int CreateItem = 0x2, UpdateDirectory = 0x1000;
    private const uint PathW = 0x5, FlushNoWait = 0x3000;

    public static void Created(string path)
    {
        try
        {
            SHChangeNotify(CreateItem, PathW | FlushNoWait, path, null);
            if (Path.GetDirectoryName(path) is { } folder) SHChangeNotify(UpdateDirectory, PathW | FlushNoWait, folder, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException) { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string? item1, string? item2);
}
