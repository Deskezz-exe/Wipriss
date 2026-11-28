using System.IO.Pipes;
using System.Text;
using Wipriss.Modules.Agents;

// Wipriss.AgentHook.exe claude          (Claude Code hook: the event comes on stdin)
// Wipriss.AgentHook.exe codex "<json>"  (Codex notify: the event is the last argument)
try
{
    HookMessage? message = null;
    if (args.Length >= 1 && args[0] == "claude")
    {
        // Claude Code's events are small; read a bounded amount so a broken pipe never keeps us alive.
        using var stdin = Console.OpenStandardInput();
        var buffer = new byte[256 * 1024];
        int total = 0, read;
        while (total < buffer.Length && (read = stdin.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        message = HookProtocol.FromClaude(Encoding.UTF8.GetString(buffer, 0, total));
    }
    else if (args.Length >= 2 && args[0] == "codex")
    {
        message = HookProtocol.FromCodex(args[^1]);
    }

    if (message is null || message.Kind == HookKind.Other) return 0;

    using var pipe = new NamedPipeClientStream(".", HookProtocol.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
    pipe.Connect(400); // Wipriss is not running: give up quickly
    var bytes = Encoding.UTF8.GetBytes(HookProtocol.Write(message));
    if (bytes.Length <= HookProtocol.MaxMessageBytes)
    {
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
    }
}
catch (Exception)
{
    // Never disturb the agent: whatever went wrong, it simply goes on.
}
return 0;
