using Wipriss.ProcessTools;

namespace Wipriss.Helper;

/// <summary>
/// Wipriss admin helper. Usage (only Wipriss starts it): Wipriss.Helper.exe stop PID:STARTTIME [PID:STARTTIME ...]
/// The exit code tells Wipriss how many processes stopped. Any other input is rejected.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return ElevatedStop.RunHelper(args);
        }
        catch
        {
            // Nothing to log: no user data passes through here. The caller sees "not stopped".
            return ElevatedStop.InvalidArguments;
        }
    }
}
