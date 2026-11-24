namespace Wipriss.ProcessTools;

public static class ProcessTree
{
    /// <summary>
    /// The given processes plus everything they started, parents first.
    /// A child counts only if it started after its parent, so a reused PID never pulls in a stranger.
    /// Console hosts (conhost.exe) are left out: Windows closes them together with their program,
    /// and counting them would make "with children" look bigger than it is.
    /// </summary>
    public static List<ProcessEntry> WithDescendants(IReadOnlyList<ProcessEntry> all, IEnumerable<ProcessEntry> roots)
    {
        var byParent = all.ToLookup(p => p.ParentPid);
        var result = new List<ProcessEntry>();
        var seen = new HashSet<int>();
        foreach (var root in roots)
            if (seen.Add(root.Pid)) result.Add(root);

        for (int i = 0; i < result.Count; i++)
        {
            var parent = result[i];
            if (parent.Pid == 0) continue; // the idle process lists itself as its own parent
            foreach (var child in byParent[parent.Pid])
                if (child.CreateTime >= parent.CreateTime && !IsConsoleHost(child) && seen.Add(child.Pid))
                    result.Add(child);
        }

        return result;
    }

    private static bool IsConsoleHost(ProcessEntry p) => p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The parent that is still running, or null if it exited (its PID may already belong to someone else).</summary>
    public static ProcessEntry? ParentOf(IReadOnlyList<ProcessEntry> all, ProcessEntry child)
    {
        foreach (var p in all)
            if (p.Pid == child.ParentPid && p.Pid != child.Pid && p.CreateTime <= child.CreateTime)
                return p;
        return null;
    }
}

/// <summary>Programs that start a dev server and may start it again if only the server is stopped.</summary>
public static class Launchers
{
    private static readonly HashSet<string> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "bun", "deno", "python", "pythonw", "py", "dotnet", "java", "ruby", "php", "cargo", "go", "air",
    };

    private static readonly string[] PackageManagers = ["npm", "npx", "yarn", "pnpm", "bun"];

    public static bool IsLauncher(string imageName, string? commandLine)
    {
        string exe = Path.GetFileNameWithoutExtension(imageName);
        if (Runners.Contains(exe)) return true;
        if (!exe.Equals("cmd", StringComparison.OrdinalIgnoreCase) || commandLine is null) return false;

        // "cmd /c npm run dev", cmd /d /s /c "npm run dev" and the npm.cmd shims
        foreach (string arg in CommandLineText.Split(commandLine))
        foreach (string word in arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = Path.GetFileNameWithoutExtension(word);
            if (PackageManagers.Any(pm => name.Equals(pm, StringComparison.OrdinalIgnoreCase))) return true;
        }

        return false;
    }
}
