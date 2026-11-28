using System.Windows.Threading;
using Wipriss.Core.Modules;
using Wipriss.Core.Settings;

namespace Wipriss.App.Shell;

/// <summary>
/// Starts modules that are switched on and stops the ones switched off. A module that is off
/// is never created, so its code is not even loaded.
/// </summary>
internal sealed class ModuleManager : IModuleHost
{
    private readonly SettingsStore _store;
    private readonly IReadOnlyList<ModuleRegistration> _registrations;
    private readonly Dictionary<string, IModule> _active = [];

    public ModuleManager(SettingsStore store, IReadOnlyList<ModuleRegistration> registrations, Dispatcher dispatcher)
    {
        _store = store;
        _registrations = registrations;
        Dispatcher = dispatcher;
        _store.Changed += Sync;
        Sync();
    }

    public Dispatcher Dispatcher { get; }

    public event Action? TimeChanged;

    public event Action<string>? OpenRequested;

    /// <summary>A module was switched off.</summary>
    public event Action<string>? Stopped;

    /// <summary>True if this module is already built (not just planned).</summary>
    public bool IsBuilt(string id) => _registrations.Any(r => r.Id == id);

    /// <summary>The running module, or null if it is unknown, not built yet or switched off.</summary>
    public IModule? Get(string id) => _active.GetValueOrDefault(id);

    public void OpenPanel(string moduleId) => OpenRequested?.Invoke(moduleId);

    public void RaiseTimeChanged() => TimeChanged?.Invoke();

    public void TrimMemory() => CrashLog.RunWhenIdle(Dispatcher, MemoryTrim.Trim);

    public void StopAll()
    {
        _store.Changed -= Sync;
        foreach (var module in _active.Values) TryStop(module);
        _active.Clear();
    }

    private void Sync()
    {
        foreach (var registration in _registrations)
        {
            bool on = _store.Current.IsModuleEnabled(registration.Id);
            if (on && !_active.ContainsKey(registration.Id))
            {
                try
                {
                    var module = registration.Create();
                    module.Start(this);
                    _active[registration.Id] = module;
                }
                catch (Exception ex)
                {
                    // One broken module must not take the island down.
                    CrashLog.Write(ex);
                }
            }
            else if (!on && _active.Remove(registration.Id, out var running))
            {
                if (running is ISwitchOffAware aware)
                {
                    try { aware.SwitchedOff(); }
                    catch (Exception ex) { CrashLog.Write(ex); }
                }
                TryStop(running);
                Stopped?.Invoke(registration.Id);
            }
        }
    }

    private static void TryStop(IModule module)
    {
        try { module.Stop(); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }
}
