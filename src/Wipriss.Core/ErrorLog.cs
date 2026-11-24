using System.IO;

namespace Wipriss.Core;

/// <summary>
/// Local error log for the app and its modules (logs\crash.log). Only exception details are written,
/// never user content, and the user's profile path is masked so the log is safe to share in an issue.
/// </summary>
public static class ErrorLog
{
    public static void Write(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var text = ex.ToString();
            if (profile.Length > 0) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never crash the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
