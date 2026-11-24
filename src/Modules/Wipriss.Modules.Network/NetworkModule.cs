using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using Wipriss.Core;
using Wipriss.Core.Modules;
using Wipriss.Core.Storage;
using Wipriss.ProcessTools;

namespace Wipriss.Modules.Network;

public sealed class TrustFile
{
    public List<string> Keys { get; set; } = [];
}

[JsonSerializable(typeof(TrustFile))]
internal sealed partial class TrustJson : JsonSerializerContext;

/// <summary>Programs the user marked as "mine, I trust it". Encrypted like the other local data.</summary>
internal sealed class TrustList
{
    private static readonly string FilePath = Path.Combine(AppPaths.LocalDir, "network-trust.dat");
    private TrustFile? _data;

    private TrustFile Data => _data ??= SecureStore.ReadJson(FilePath, TrustJson.Default.TrustFile) ?? new TrustFile();

    public int Count => Data.Keys.Count;

    public bool Contains(string key) => Data.Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public void Add(string key)
    {
        if (Contains(key)) return;
        Data.Keys.Add(key);
        Save();
    }

    public void Clear()
    {
        Data.Keys.Clear();
        Save();
    }

    public void Forget() => _data = null;

    private void Save() => SecureStore.WriteJson(FilePath, Data, TrustJson.Default.TrustFile);
}

/// <summary>
/// Who uses the network and who looks suspicious. Nothing runs in the background: the check runs when
/// you open the island (at most every 5 minutes) and every few seconds while the panel is open.
/// </summary>
public sealed class NetworkModule : IModule
{
    private readonly NetworkAnalyzer _analyzer = new();
    private readonly TrustList _trust = new();
    private Task? _checking;

    public ModuleInfo Info { get; } = ModuleCatalog.Get("network");

    public Size PanelSize => new(520, 660);

    /// <summary>Never waits: shows the last result and starts a fresh check in the background if it is old.</summary>
    public string Summary
    {
        get
        {
            var last = _analyzer.Last;
            if ((last is null || DateTime.Now - last.MadeAt > TimeSpan.FromMinutes(5)) && _checking is not { IsCompleted: false })
                _checking = Task.Run(() =>
                {
                    try { _analyzer.Analyze(); }
                    catch (Exception ex) { ErrorLog.Write(ex); }
                });

            if (last is null) return "проверка…";
            int flagged = last.Apps.Count(a => a.Verdict.Level != ThreatLevel.None && !_trust.Contains(a.Key));
            return flagged > 0 ? $"внимание: {flagged}" : $"{TrafficCheck.Bytes(last.Apps.Sum(a => a.TotalToday))} сегодня";
        }
    }

    public void Start(IModuleHost host) { }

    public void Stop()
    {
        _analyzer.Reset();
        _trust.Forget();
    }

    public FrameworkElement CreatePanel(IPanelContext panel) => new NetworkPanel(_analyzer, _trust, panel);
}
