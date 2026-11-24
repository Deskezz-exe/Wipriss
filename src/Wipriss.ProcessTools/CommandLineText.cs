using System.Runtime.InteropServices;

namespace Wipriss.ProcessTools;

/// <summary>Turns a long command line into something a person recognizes, like "node vite" or "python manage.py runserver".</summary>
public static class CommandLineText
{
    // Programs that run a script: the script tells more than the program.
    private static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "bun", "deno", "python", "pythonw", "py", "java", "javaw", "dotnet", "ruby", "php", "perl",
    };

    /// <summary>Splits a command line the way Windows programs do.</summary>
    public static string[] Split(string commandLine)
    {
        // CommandLineToArgvW returns the current program's path for an empty string.
        if (string.IsNullOrWhiteSpace(commandLine)) return [];

        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) return [commandLine];
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <param name="imageName">Image name from the process list, for example "node.exe".</param>
    public static string Short(string? commandLine, string imageName)
    {
        string exe = Path.GetFileNameWithoutExtension(imageName);
        if (string.IsNullOrWhiteSpace(commandLine) || !Runtimes.Contains(exe)) return exe;

        string[] args = Split(commandLine);
        var parts = new List<string> { exe };
        int i = 1;
        bool found = false;
        for (; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "-jar" && i + 1 < args.Length)
            {
                // python -m uvicorn, java -jar app.jar
                parts.Add(ScriptName(args[i + 1]));
                i += 2;
                found = true;
                break;
            }

            if (!arg.StartsWith('-'))
            {
                parts.Add(ScriptName(arg));
                i++;
                found = true;
                break;
            }
        }

        if (!found) return exe;

        // Up to two short sub-commands after the script, like "run dev" or "runserver".
        for (int taken = 0; i < args.Length && taken < 2; i++, taken++)
        {
            string arg = args[i];
            if (arg.Length is 0 or > 16 || arg.StartsWith('-') || arg.IndexOfAny(['\\', '/', ':', '=', '.']) >= 0) break;
            parts.Add(arg);
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// "…\node_modules\vite\bin\vite.js" → "vite", "…\node_modules\@angular\cli\bin\ng.js" → "@angular/cli",
    /// "C:\app\manage.py" → "manage.py".
    /// </summary>
    public static string ScriptName(string arg)
    {
        string path = arg.Replace('/', '\\');
        const string marker = "\\node_modules\\";
        int at = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            string[] rest = path[(at + marker.Length)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length >= 2 && rest[0] == ".bin") return Path.GetFileNameWithoutExtension(rest[1]);
            if (rest.Length >= 2 && rest[0].StartsWith('@')) return $"{rest[0]}/{rest[1]}";
            if (rest.Length >= 1) return rest[0];
        }

        string file = Path.GetFileName(path.TrimEnd('\\'));
        return file.Length == 0 ? arg : file;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
