using System.IO;
using System.Text;
using System.Text.Json;

namespace Wipriss.Converter;

/// <summary>
/// Wipriss.Converter.exe: reads one <see cref="ConvertRequest"/> as JSON from stdin, converts the
/// files one by one, writes a <see cref="ConvertReply"/> line for each and exits. No window, no
/// network, no arguments: only Wipriss starts it.
/// </summary>
internal static class Program
{
    private const int MaxFiles = 500;

    private static int Main()
    {
        ConvertRequest? request;
        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            request = JsonSerializer.Deserialize(stdin.ReadToEnd(), ProtocolJson.Default.ConvertRequest);
        }
        catch (JsonException)
        {
            return 2;
        }
        if (request is null) return 2;
        var jobs = request.AllJobs();
        if (jobs.Count is 0 or > MaxFiles) return 2;

        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new Lock();
        bool gone = false;
        bool Send(ConvertReply reply)
        {
            lock (gate)
            {
                if (gone) return false;
                try { stdout.WriteLine(JsonSerializer.Serialize(reply, ProtocolJson.Default.ConvertReply)); }
                catch (IOException) { gone = true; }
                return !gone;
            }
        }

        for (int i = 0; i < jobs.Count; i++)
        {
            int index = i;
            var reply = ImageConverter.ConvertFile(index, jobs[index], request,
                percent => Send(new ConvertReply { Index = index, Progress = percent }));
            if (!Send(reply)) return 3; // Wipriss is gone: nobody is waiting for the rest
        }
        return 0;
    }
}
