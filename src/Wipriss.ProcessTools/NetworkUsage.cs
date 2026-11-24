using System.Runtime.InteropServices;

namespace Wipriss.ProcessTools;

/// <summary>Traffic of one program as Windows counts it.</summary>
/// <param name="Id">A device path for desktop programs ("\device\harddiskvolume3\…\firefox.exe"),
/// a package family name for Store apps, or empty for Windows itself.</param>
public sealed record AppTraffic(string Id, long Received, long Sent)
{
    public long Total => Received + Sent;
}

/// <summary>
/// Per-program traffic from Windows' own accounting, the one behind Settings → Network → Data usage.
/// No admin rights needed and nothing is captured: Windows already keeps these numbers.
/// Windows updates them in batches, roughly once an hour, so this is for "how much today", not live speed.
/// </summary>
/// <remarks>
/// Calls the WinRT API ConnectionProfile.GetAttributedNetworkUsageAsync directly through its COM
/// interfaces. The official .NET projection would add over 20 MB to a 1 MB app.
/// Interface IDs and method order are taken from the Windows metadata (Windows.Networking.Connectivity).
/// </remarks>
public static unsafe class NetworkUsage
{
    private static readonly Guid IID_INetworkInformationStatics = new("5074f851-950d-4165-9c15-365619481eea");
    private static readonly Guid IID_IConnectionProfile3 = new("578c2528-4cd9-4161-8045-201cfd5b115c");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    // IInspectable has 6 slots; each interface's own methods follow.
    private const int GetConnectionProfiles = 6;        // INetworkInformationStatics
    private const int GetAttributedNetworkUsage = 6;    // IConnectionProfile3
    private const int VectorGetAt = 6, VectorSize = 7;  // IVectorView<T>
    private const int AsyncGetResults = 8;              // IAsyncOperation<T>
    private const int AsyncStatus = 7, AsyncErrorCode = 8, AsyncCancel = 9, AsyncClose = 10; // IAsyncInfo
    private const int UsageBytesSent = 6, UsageBytesReceived = 7, UsageAttributionId = 8; // IAttributedNetworkUsage

    /// <summary>Totals per program for the time range, over every network this PC has used. Call from a thread-pool thread.</summary>
    public static List<AppTraffic> Read(DateTime fromUtc, DateTime toUtc)
    {
        var totals = new Dictionary<string, (long Received, long Sent)>(StringComparer.OrdinalIgnoreCase);
        IntPtr statics = Activate("Windows.Networking.Connectivity.NetworkInformation", IID_INetworkInformationStatics);
        try
        {
            IntPtr profiles;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(statics, GetConnectionProfiles))(statics, &profiles));
            try
            {
                uint count;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(profiles, VectorSize))(profiles, &count));
                for (uint i = 0; i < count; i++)
                {
                    IntPtr profile;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(profiles, VectorGetAt))(profiles, i, &profile));
                    try
                    {
                        ReadProfile(profile, fromUtc, toUtc, totals);
                    }
                    catch (COMException)
                    {
                        // A network without usage data (for example, never connected). Skip it.
                    }
                    finally
                    {
                        Release(profile);
                    }
                }
            }
            finally
            {
                Release(profiles);
            }
        }
        finally
        {
            Release(statics);
        }

        return totals.Select(t => new AppTraffic(t.Key, t.Value.Received, t.Value.Sent)).ToList();
    }

    private static void ReadProfile(IntPtr profile, DateTime fromUtc, DateTime toUtc, Dictionary<string, (long, long)> totals)
    {
        IntPtr profile3 = QueryInterface(profile, IID_IConnectionProfile3);
        try
        {
            IntPtr operation;
            var states = new UsageStates(); // roaming and shared: "don't care"
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, long, UsageStates, IntPtr*, int>)Slot(profile3, GetAttributedNetworkUsage))(
                profile3, fromUtc.ToFileTimeUtc(), toUtc.ToFileTimeUtc(), states, &operation));
            try
            {
                IntPtr list = WaitForResults(operation);
                try
                {
                    uint count;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(list, VectorSize))(list, &count));
                    for (uint i = 0; i < count; i++)
                    {
                        IntPtr usage;
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(list, VectorGetAt))(list, i, &usage));
                        try
                        {
                            ulong sent, received;
                            IntPtr id;
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, ulong*, int>)Slot(usage, UsageBytesSent))(usage, &sent));
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, ulong*, int>)Slot(usage, UsageBytesReceived))(usage, &received));
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(usage, UsageAttributionId))(usage, &id));
                            string key = TakeString(id);
                            totals.TryGetValue(key, out var sum);
                            totals[key] = ((long)received + sum.Item1, (long)sent + sum.Item2);
                        }
                        finally
                        {
                            Release(usage);
                        }
                    }
                }
                finally
                {
                    Release(list);
                }
            }
            finally
            {
                Release(operation);
            }
        }
        finally
        {
            Release(profile3);
        }
    }

    /// <summary>Waits for the async call (it takes tens of milliseconds) and returns its result.</summary>
    private static IntPtr WaitForResults(IntPtr operation)
    {
        IntPtr info = QueryInterface(operation, IID_IAsyncInfo);
        try
        {
            var deadline = Environment.TickCount64 + 10_000;
            int status;
            while (true)
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, AsyncStatus))(info, &status));
                if (status != 0) break; // 0 = started
                if (Environment.TickCount64 > deadline)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(info, AsyncCancel))(info);
                    throw new TimeoutException("Windows did not return network usage in time.");
                }

                Thread.Sleep(2);
            }

            if (status != 1) // 1 = completed, 2 = canceled, 3 = error
            {
                int error;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, AsyncErrorCode))(info, &error));
                Marshal.ThrowExceptionForHR(error != 0 ? error : unchecked((int)0x80004004)); // E_ABORT
            }

            IntPtr result;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(operation, AsyncGetResults))(operation, &result));
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(info, AsyncClose))(info);
            return result;
        }
        finally
        {
            Release(info);
        }
    }

    private static IntPtr Activate(string className, Guid iid)
    {
        Check(WindowsCreateString(className, className.Length, out IntPtr name));
        try
        {
            Check(RoGetActivationFactory(name, in iid, out IntPtr factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    private static IntPtr Slot(IntPtr instance, int index) => (*(IntPtr**)instance)[index];

    private static IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(instance, 0))(instance, &iid, &result));
        return result;
    }

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, 2))(instance);
    }

    /// <summary>Reads an HSTRING the callee gave us and frees it.</summary>
    private static string TakeString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return "";
        try
        {
            IntPtr buffer = WindowsGetStringRawBuffer(hstring, out uint length);
            return buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(buffer, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsageStates
    {
        public int Roaming;
        public int Shared;
    }

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
}
