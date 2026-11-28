using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wipriss.Modules.Agents;

/// <summary>Where the hook stands for one agent.</summary>
public enum HookState { NotFound, Connected, Off, OldPath, Broken, Busy, Failed, NoHookFile }

/// <summary>
/// Adds or removes Wipriss's hook in the agents' own settings: on its own when the Agents module starts (the owner
/// asked for no buttons) and out again when the module is switched off. Everything else in the file is kept, a copy
/// of the old file is saved next to it first, and the new one replaces it in one step. The file is written only
/// when our entry is missing or points to an old place. Our entry is recognised by the hook's file name.
/// </summary>
public static class HookSetup
{
    public const string HookFile = "Wipriss.AgentHook.exe";
    private const string CodexComment = "# Wipriss: tells the island when Codex waits or is done";

    public static string HookPath => Path.Combine(AppContext.BaseDirectory, HookFile);

    public static string ClaudeSettingsFile
    {
        get
        {
            string? config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            string dir = string.IsNullOrWhiteSpace(config)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : config;
            return Path.Combine(dir, "settings.json");
        }
    }

    public static string CodexConfigFile
    {
        get
        {
            string? home = Environment.GetEnvironmentVariable("CODEX_HOME");
            string dir = string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : home;
            return Path.Combine(dir, "config.toml");
        }
    }

    public static bool ClaudeInstalled => Directory.Exists(Path.GetDirectoryName(ClaudeSettingsFile));

    public static bool CodexInstalled => Directory.Exists(Path.GetDirectoryName(CodexConfigFile));

    // ---- Claude Code: hooks.Notification in settings.json ----

    /// <summary>The command line Claude Code runs. Forward slashes work both in Git Bash and cmd.</summary>
    public static string ClaudeCommand(string hookPath) => $"\"{hookPath.Replace('\\', '/')}\" claude";

    public static bool HasClaudeHook(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { return JsonNode.Parse(json, documentOptions: ReadOptions) is JsonObject root && Ours(root).Any(); }
        catch (JsonException) { return false; }
    }

    /// <summary>Returns the settings with our hook (any old copy of it replaced). Throws <see cref="FormatException"/> on a broken file.</summary>
    public static string AddClaudeHook(string? json, string hookPath)
    {
        var root = ParseClaude(json);
        RemoveOurs(root);
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            if (root["hooks"] is not null) throw new FormatException("\"hooks\" is not an object.");
            root["hooks"] = hooks = [];
        }
        var list = hooks["Notification"] as JsonArray;
        if (list is null)
        {
            if (hooks["Notification"] is not null) throw new FormatException("\"Notification\" is not a list.");
            hooks["Notification"] = list = [];
        }
        list.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = ClaudeCommand(hookPath),
            }),
        });
        return root.ToJsonString(WriteOptions);
    }

    public static string RemoveClaudeHook(string json)
    {
        var root = ParseClaude(json);
        RemoveOurs(root);
        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject ParseClaude(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject
                   ?? throw new FormatException("The settings file is not a JSON object.");
        }
        catch (JsonException ex) { throw new FormatException(ex.Message, ex); }
    }

    /// <summary>Our command entries inside hooks.Notification.</summary>
    private static IEnumerable<(JsonArray Inner, JsonNode Entry)> Ours(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks || hooks["Notification"] is not JsonArray list) yield break;
        foreach (var group in list)
            if (group is JsonObject g && g["hooks"] is JsonArray inner)
                foreach (var entry in inner)
                    if (entry is JsonObject e && e["command"] is JsonValue v && v.TryGetValue(out string? cmd)
                        && cmd.Contains(HookFile, StringComparison.OrdinalIgnoreCase))
                        yield return (inner, entry);
    }

    private static void RemoveOurs(JsonObject root)
    {
        foreach (var (inner, entry) in Ours(root).ToList()) inner.Remove(entry);
        if (root["hooks"] is not JsonObject hooks || hooks["Notification"] is not JsonArray list) return;
        // Tidy only what we emptied.
        foreach (var group in list.ToList())
            if (group is JsonObject g && g["hooks"] is JsonArray { Count: 0 }) list.Remove(group);
        if (list.Count == 0) hooks.Remove("Notification");
        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep non-Latin text readable in the user's file
    };

    // ---- Codex: notify = [...] at the top of config.toml ----

    public static bool HasCodexNotify(string? toml) =>
        toml is not null && Lines(toml).Any(l => IsNotify(l) && l.Contains(HookFile, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the config with our notify line. Throws <see cref="InvalidOperationException"/> when Codex already
    /// runs another program there: that one is the user's, and Codex has room for only one.
    /// </summary>
    public static string AddCodexNotify(string? toml, string hookPath)
    {
        var lines = Lines(RemoveCodexNotify(toml ?? "")).ToList();
        int firstTable = lines.FindIndex(l => l.TrimStart().StartsWith('['));
        int top = firstTable < 0 ? lines.Count : firstTable;
        if (lines.Take(top).Any(IsNotify)) throw new InvalidOperationException("Codex already has its own notify program.");

        // A TOML literal string ('...') needs no escaping of backslashes; a path cannot hold a quote.
        string notify = $"notify = ['{hookPath}', 'codex']";
        // Top-level keys must come before the first [table].
        var block = new List<string> { CodexComment, notify };
        if (top > 0 && lines[top - 1].Trim().Length > 0) block.Insert(0, "");
        if (firstTable >= 0) block.Add("");
        // An empty file is one empty line: do not keep it in front of ours.
        if (lines is [""]) lines.Clear();
        lines.InsertRange(Math.Min(top, lines.Count), block);
        string nl = (toml ?? "").Contains("\r\n") ? "\r\n" : "\n";
        return string.Join(nl, lines).TrimEnd('\r', '\n') + nl;
    }

    public static string RemoveCodexNotify(string toml)
    {
        if (!HasCodexNotify(toml)) return toml;
        var lines = Lines(toml).ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (!(IsNotify(lines[i]) && lines[i].Contains(HookFile, StringComparison.OrdinalIgnoreCase))) continue;
            lines.RemoveAt(i);
            if (i > 0 && lines[i - 1].Trim() == CodexComment) { lines.RemoveAt(i - 1); i--; }
            // Our blank line around it.
            if (i > 0 && i < lines.Count && lines[i - 1].Trim().Length == 0 && lines[i].Trim().Length == 0) lines.RemoveAt(i);
        }
        string nl = toml.Contains("\r\n") ? "\r\n" : "\n";
        string text = string.Join(nl, lines).Trim('\r', '\n');
        return text.Length == 0 ? "" : text + nl;
    }

    private static bool IsNotify(string line)
    {
        var t = line.TrimStart();
        if (!t.StartsWith("notify", StringComparison.Ordinal)) return false;
        t = t[6..].TrimStart();
        return t.StartsWith('=');
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // ---- On its own: at start (and when the exe moved), off when the module is switched off ----

    /// <summary>True when the file already runs exactly this hook, so nothing needs writing.</summary>
    public static bool ClaudeHookIsCurrent(string? json, string hookPath)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            if (JsonNode.Parse(json, documentOptions: ReadOptions) is not JsonObject root) return false;
            var ours = Ours(root).ToList();
            return ours.Count == 1 && ours[0].Entry["command"]?.GetValue<string>() == ClaudeCommand(hookPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    public static bool CodexNotifyIsCurrent(string? toml, string hookPath) =>
        toml is not null && Lines(toml).Count(l => IsNotify(l) && l.Contains(HookFile, StringComparison.OrdinalIgnoreCase)) == 1
        && Lines(toml).Any(l => IsNotify(l) && l.Contains($"'{hookPath}'", StringComparison.Ordinal));

    /// <summary>Adds the hook for every agent found, or fixes its path. Writes only what must change. Runs on a worker thread.</summary>
    public static (HookState Claude, HookState Codex) Connect()
    {
        string hook = HookPath;
        bool hasExe = File.Exists(hook);
        var claude = !ClaudeInstalled ? HookState.NotFound
            : Apply(ClaudeSettingsFile, hasExe, t => ClaudeHookIsCurrent(t, hook), t => AddClaudeHook(t, hook));
        var codex = !CodexInstalled ? HookState.NotFound
            : Apply(CodexConfigFile, hasExe, t => CodexNotifyIsCurrent(t, hook), t => AddCodexNotify(t, hook));
        return (claude, codex);
    }

    /// <summary>Takes our hook out of both agents' settings (the user switched the module off).</summary>
    public static void Disconnect()
    {
        foreach (var (file, has, remove) in new (string, Func<string, bool>, Func<string, string>)[]
                 {
                     (ClaudeSettingsFile, HasClaudeHook, RemoveClaudeHook),
                     (CodexConfigFile, HasCodexNotify, RemoveCodexNotify),
                 })
        {
            try
            {
                if (!File.Exists(file) || !has(File.ReadAllText(file))) continue;
                Change(file, old => old is null ? "" : remove(old));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { }
        }
    }

    /// <summary>What the Settings page shows. Reads only.</summary>
    public static (HookState Claude, HookState Codex) Inspect()
    {
        string hook = HookPath;
        HookState Look(bool installed, string file, Func<string?, bool> current, Func<string?, bool> any)
        {
            if (!installed) return HookState.NotFound;
            try
            {
                string? text = File.Exists(file) ? File.ReadAllText(file) : null;
                return current(text) ? HookState.Connected : any(text) ? HookState.OldPath : HookState.Off;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return HookState.Failed; }
        }
        return (Look(ClaudeInstalled, ClaudeSettingsFile, t => ClaudeHookIsCurrent(t, hook), HasClaudeHook),
                Look(CodexInstalled, CodexConfigFile, t => CodexNotifyIsCurrent(t, hook), HasCodexNotify));
    }

    private static HookState Apply(string file, bool hasExe, Func<string?, bool> current, Func<string?, string> add)
    {
        try
        {
            string? text = File.Exists(file) ? File.ReadAllText(file) : null;
            if (current(text)) return HookState.Connected;
            if (!hasExe) return HookState.NoHookFile;
            Change(file, add);
            return HookState.Connected;
        }
        catch (FormatException) { return HookState.Broken; }
        catch (InvalidOperationException) { return HookState.Busy; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return HookState.Failed; }
    }

    // ---- Files ----

    /// <summary>Reads the file (null if there is none), changes it, keeps a copy of the old one and writes the new one in one step.</summary>
    public static void Change(string file, Func<string?, string> change)
    {
        string? old = File.Exists(file) ? File.ReadAllText(file) : null;
        string next = change(old);
        if (next == old) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (old is not null) File.Copy(file, file + ".wipriss-backup", overwrite: true);
        string temp = file + ".wipriss-new";
        File.WriteAllText(temp, next, new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }
}
