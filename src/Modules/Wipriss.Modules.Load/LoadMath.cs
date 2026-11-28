using System.Globalization;

namespace Wipriss.Modules.Load;

/// <summary>The arithmetic of the load figures, apart from Windows calls, so it is covered by tests.</summary>
internal static class LoadMath
{
    /// <summary>
    /// CPU busy share from two GetSystemTimes readings (100 ns units). Kernel time includes idle time.
    /// </summary>
    public static double CpuPercent(ulong idle0, ulong kernel0, ulong user0, ulong idle1, ulong kernel1, ulong user1)
    {
        double total = (double)(kernel1 - kernel0) + (user1 - user0);
        if (total <= 0) return 0;
        double busy = total - (idle1 - idle0);
        return Math.Clamp(busy * 100 / total, 0, 100);
    }

    /// <summary>
    /// GPU load like Task Manager: the busiest engine of any adapter, each engine summed over processes.
    /// Instance names look like "pid_1234_luid_0x00000000_0x0000D1B6_phys_0_eng_3_engtype_VideoDecode".
    /// </summary>
    public static double GpuPercent(IEnumerable<(string Instance, double Value)> items)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in items)
        {
            if (double.IsNaN(value) || value <= 0) continue;
            int luid = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int type = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (luid < 0) continue;
            string engine = type > luid ? instance[luid..type] : instance[luid..];
            engines[engine] = engines.GetValueOrDefault(engine) + value;
        }
        return engines.Count == 0 ? 0 : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    /// <summary>Bytes per second as a short figure: "0K", "850K", "1.2M", "12M", "1.1G".</summary>
    public static string Speed(double bytesPerSecond)
    {
        var ci = CultureInfo.InvariantCulture;
        double k = Math.Max(0, bytesPerSecond) / 1024;
        if (k < 999.5) return Math.Round(k).ToString("0", ci) + "K";
        double m = k / 1024;
        if (m < 9.95) return m.ToString("0.0", ci) + "M";
        if (m < 999.5) return Math.Round(m).ToString("0", ci) + "M";
        return (m / 1024).ToString("0.0", ci) + "G";
    }

    public static string Percent(double value) => Math.Round(Math.Clamp(value, 0, 100)).ToString("0", CultureInfo.InvariantCulture) + "%";
}
