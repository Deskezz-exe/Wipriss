using Wipriss.App.Interop;
using Wipriss.Core.Input;
using Wipriss.Core.Settings;

namespace Wipriss.App.Shell;

/// <summary>
/// Global hotkeys that open a module's panel (RegisterHotKey: Windows tells us by a message, nothing polls).
/// Re-registers whenever settings change.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    /// <summary>Modules opened by a hotkey, with the default combination. Win+Alt is free in Windows and
    /// does not clash with AltGr letters (Ctrl+Alt does, on Polish and other layouts).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["notes"] = "Win+Alt+N",
    };

    private readonly IntPtr _hwnd;
    private readonly SettingsStore _store;
    private readonly Dictionary<int, string> _registered = [];
    private readonly HashSet<string> _failed = [];
    private bool _suspended;

    public HotkeyService(IntPtr hwnd, SettingsStore store)
    {
        _hwnd = hwnd;
        _store = store;
        _store.Changed += Apply;
        Apply();
    }

    public event Action<string>? Pressed;

    /// <summary>Raised after registering, so an open Settings window can show which keys are taken.</summary>
    public event Action? Applied;

    public static string Current(AppSettings settings, string moduleId) =>
        settings.HotkeyFor(moduleId, Defaults.GetValueOrDefault(moduleId, ""));

    /// <summary>The key is taken by another program (or Windows).</summary>
    public bool IsTaken(string moduleId) => _failed.Contains(moduleId);

    /// <summary>While the user records a new combination, the old one must not fire.</summary>
    public void Suspend(bool suspend)
    {
        _suspended = suspend;
        Apply();
    }

    /// <summary>Called from the window procedure. True if it was ours.</summary>
    public bool Handle(int id)
    {
        if (!_registered.TryGetValue(id, out var module)) return false;
        Pressed?.Invoke(module);
        return true;
    }

    private void Apply()
    {
        foreach (var id in _registered.Keys) Native.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        _failed.Clear();

        if (!_suspended)
        {
            int next = 1;
            foreach (var module in Defaults.Keys)
            {
                if (!_store.Current.IsModuleEnabled(module)) continue;
                var key = Hotkey.Parse(Current(_store.Current, module));
                if (!key.IsUsable) continue;
                int id = next++;
                if (Native.RegisterHotKey(_hwnd, id, key.Modifiers | Native.MOD_NOREPEAT, key.Key)) _registered[id] = module;
                else _failed.Add(module);
            }
        }

        Applied?.Invoke();
    }

    public void Dispose()
    {
        _store.Changed -= Apply;
        foreach (var id in _registered.Keys) Native.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }
}
