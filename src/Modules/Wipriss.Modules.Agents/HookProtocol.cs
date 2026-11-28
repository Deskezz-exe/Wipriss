using System.Diagnostics;
using System.Text.Json;

namespace Wipriss.Modules.Agents;

/// <summary>What an agent's hook tells the island.</summary>
public enum HookKind { Waiting, Done, Other }

/// <summary>One event from Claude Code or Codex, as the hook passes it on.</summary>
/// <param name="Agent">"claude" or "codex".</param>
/// <param name="Session">The agent's session id (may be empty).</param>
/// <param name="Transcript">Claude Code's transcript file, if it said.</param>
/// <param name="Project">Last folder name of the working directory, if any.</param>
public sealed record HookMessage(string Agent, HookKind Kind, string Session, string Transcript, string Project);

/// <summary>
/// The tiny protocol between <c>Wipriss.AgentHook.exe</c> and the Agents module: one JSON line over a named pipe
/// that only this Windows user can open. Shared by both sides (linked into the hook's project).
/// Everything here is untrusted text: it is only shown, never run or used as a path to write.
/// </summary>
public static class HookProtocol
{
    public const int MaxMessageBytes = 8 * 1024;
    private const int MaxField = 400;

    public static string PipeName => $"Wipriss.Agents.{Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Claude Code passes a JSON object on stdin. Notification with "permission_prompt" (or a question to answer)
    /// means it waits for you; "idle_prompt" only means it is done and idle.
    /// </summary>
    public static HookMessage? FromClaude(string json)
    {
        try
        {
            // Some shells put a byte-order mark in front of piped text.
            using var doc = JsonDocument.Parse(json.TrimStart('﻿', ' ', '\r', '\n', '\t'));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string evt = Str(root, "hook_event_name");
            string type = Str(root, "notification_type");
            string message = Str(root, "message");
            var kind = evt switch
            {
                "Notification" when type is "permission_prompt" or "elicitation_dialog" => HookKind.Waiting,
                "Notification" when type.Length == 0 && message.Contains("permission", StringComparison.OrdinalIgnoreCase) => HookKind.Waiting,
                "Notification" when type == "idle_prompt" => HookKind.Done,
                "Stop" => HookKind.Done,
                _ => HookKind.Other,
            };
            return new HookMessage("claude", kind, Str(root, "session_id"), Str(root, "transcript_path"), ProjectOf(Str(root, "cwd")));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Codex runs the <c>notify</c> program with a JSON object as its last argument. Today it sends
    /// "agent-turn-complete"; anything about an approval counts as waiting.
    /// </summary>
    public static HookMessage? FromCodex(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.TrimStart('﻿', ' ', '\r', '\n', '\t'));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string type = Str(root, "type");
            var kind = type == "agent-turn-complete" ? HookKind.Done
                : type.Contains("approval", StringComparison.OrdinalIgnoreCase) ? HookKind.Waiting
                : HookKind.Other;
            string session = Str(root, "thread-id");
            if (session.Length == 0) session = Str(root, "session-id");
            return new HookMessage("codex", kind, session, "", ProjectOf(Str(root, "cwd")));
        }
        catch (JsonException) { return null; }
    }

    public static string Write(HookMessage m) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["agent"] = m.Agent,
        ["kind"] = m.Kind.ToString(),
        ["session"] = m.Session,
        ["transcript"] = m.Transcript,
        ["project"] = m.Project,
    });

    public static HookMessage? Read(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string agent = Str(root, "agent");
            if (agent is not ("claude" or "codex")) return null;
            if (!Enum.TryParse<HookKind>(Str(root, "kind"), out var kind)) return null;
            return new HookMessage(agent, kind, Str(root, "session"), Str(root, "transcript"), Str(root, "project"));
        }
        catch (JsonException) { return null; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString() ?? "") : "";

    private static string Clip(string s) => s.Length > MaxField ? s[..MaxField] : s;

    private static string ProjectOf(string cwd)
    {
        cwd = cwd.TrimEnd('\\', '/');
        int cut = Math.Max(cwd.LastIndexOf('\\'), cwd.LastIndexOf('/'));
        string name = cut >= 0 ? cwd[(cut + 1)..] : cwd;
        return name.Length > 60 ? name[..60] : name;
    }
}
