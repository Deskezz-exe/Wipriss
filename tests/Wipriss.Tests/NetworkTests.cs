using System.IO;
using System.Net;
using Wipriss.ProcessTools;

namespace Wipriss.Tests;

public class TrafficCheckTests
{
    private const long MB = 1024 * 1024;
    private static readonly FolderHints Folders = new(
    [
        (@"D:\Profile\AppData\Local\Temp", "Temp", 2),
        (@"D:\Profile\Downloads", "Загрузки", 2),
        (@"D:\Profile\AppData\Roaming", "AppData\\Roaming", 1),
    ]);

    private static readonly FileSignature Signed = new(SignatureState.Signed, "Some Vendor");
    private static readonly FileSignature Unsigned = new(SignatureState.Unsigned, null);

    private static Verdict Check(TrafficFacts facts) => TrafficCheck.Evaluate(facts, Folders);

    private static ConnectionEntry To(int port, int lastOctet = 10) => new(IPAddress.Parse($"203.0.113.{lastOctet}"), port, 1234);

    [Fact]
    public void Notepad_moving_247_MB_is_suspicious_even_though_windows_signed_it()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"C:\Windows\System32\notepad.exe", FileName = "notepad.exe", InWindowsFolder = true,
            Signature = new FileSignature(SignatureState.Signed, "Microsoft Windows"),
            ReceivedToday = 7 * MB, SentToday = 240 * MB,
        });

        Assert.NotEqual(ThreatLevel.None, verdict.Level);
        Assert.Contains(verdict.Reasons, r => r.Text.Contains("Блокнот"));
    }

    [Fact]
    public void Store_notepad_is_matched_by_package_name() =>
        Assert.NotEqual(ThreatLevel.None, Check(new TrafficFacts
        {
            FileName = "Microsoft.WindowsNotepad_8wekyb3d8bbwe", IsStoreApp = true, ReceivedToday = 300 * MB,
        }).Level);

    [Fact]
    public void Busy_signed_browser_is_fine()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"C:\Program Files\Mozilla Firefox\firefox.exe", FileName = "firefox.exe", Signature = Signed,
            ReceivedToday = 2000 * MB, SentToday = 150 * MB, UsualDailyMax = 1500 * MB, ActiveDays = 7, UsualSentShare = 0.1,
            Connections = Enumerable.Range(1, 40).Select(i => To(443, i)).ToList(),
        });

        Assert.Equal(ThreatLevel.None, verdict.Level);
    }

    [Fact]
    public void Upload_heavy_app_that_always_uploads_is_fine()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"C:\Program Files\Chat\chat.exe", FileName = "chat.exe", Signature = Signed,
            ReceivedToday = 40 * MB, SentToday = 190 * MB, UsualDailyMax = 300 * MB, ActiveDays = 6, UsualSentShare = 0.8,
        });

        Assert.Equal(ThreatLevel.None, verdict.Level);
    }

    [Fact]
    public void Fake_svchost_in_downloads_is_highly_suspicious()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"D:\Profile\Downloads\svchost.exe", FileName = "svchost.exe", Signature = Unsigned, ReceivedToday = 2 * MB,
        });

        Assert.Equal(ThreatLevel.High, verdict.Level);
        Assert.Contains(verdict.Reasons, r => r.Text.Contains("Называется как файл Windows"));
        Assert.Contains(verdict.Reasons, r => r.Text.Contains("Загрузки"));
    }

    [Fact]
    public void Miner_pattern_is_highly_suspicious()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"D:\Profile\AppData\Roaming\update\helper.exe", FileName = "helper.exe", Signature = Unsigned,
            ReceivedToday = 30 * MB, SentToday = 20 * MB, Connections = [To(3333)], CpuPercent = 85,
        });

        Assert.Equal(ThreatLevel.High, verdict.Level);
        Assert.Contains(verdict.Reasons, r => r.Text.Contains("3333"));
        Assert.Contains(verdict.Reasons, r => r.Text.Contains("85 %"));
    }

    [Fact]
    public void Lookalike_name_is_noticed()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"C:\Program Files\Thing\svch0st.exe", FileName = "svch0st.exe", Signature = Unsigned, ReceivedToday = 5 * MB,
        });

        Assert.Equal(ThreatLevel.Suspicious, verdict.Level);
        Assert.Contains(verdict.Reasons, r => r.Text.Contains("svchost"));
    }

    [Fact]
    public void Sudden_spike_and_upload_are_reasons()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"C:\Program Files\Tool\tool.exe", FileName = "tool.exe", Signature = Unsigned,
            ReceivedToday = 10 * MB, SentToday = 900 * MB, UsualDailyMax = 20 * MB, ActiveDays = 5, UsualSentShare = 0.1,
        });

        Assert.Equal(ThreatLevel.High, verdict.Level);
        Assert.Contains(verdict.Reasons, r => r.Text.Contains("обычно не больше"));
        Assert.Contains(verdict.Reasons, r => r.Text.Contains("Отправил"));
    }

    [Fact]
    public void Tampered_signature_counts_more_than_none()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"C:\Program Files\App\app.exe", FileName = "app.exe", Signature = new FileSignature(SignatureState.Broken, null), ReceivedToday = MB,
        });

        Assert.Equal(ThreatLevel.Suspicious, verdict.Level);
    }

    [Fact]
    public void Script_started_by_word_is_suspicious()
    {
        var verdict = Check(new TrafficFacts
        {
            Path = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", FileName = "powershell.exe", InWindowsFolder = true,
            ReceivedToday = 2 * MB, ParentName = "WINWORD.EXE",
        });

        Assert.Equal(ThreatLevel.Suspicious, verdict.Level);
    }

    [Theory]
    [InlineData("svch0st", "svchost", true)]
    [InlineData("scvhost", "svchost", true)]
    [InlineData("svchosts", "svchost", true)]
    [InlineData("svchost", "svchost", false)]
    [InlineData("chrome", "svchost", false)]
    public void One_edit_apart(string a, string b, bool expected) => Assert.Equal(expected, TrafficCheck.OneEditApart(a, b));

    [Theory]
    [InlineData(500L, "1 КБ")]
    [InlineData(247L * MB, "247 МБ")]
    [InlineData(1536L * MB, "1,5 ГБ")]
    public void Bytes_read_naturally(long bytes, string expected) => Assert.Equal(expected, TrafficCheck.Bytes(bytes));
}

public class FileTrustTests
{
    private static string DotnetExe => Path.Combine(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..")), "dotnet.exe");

    [Fact]
    public void Signed_program_has_its_publisher()
    {
        var signature = FileTrust.Check(DotnetExe);
        Assert.Equal(SignatureState.Signed, signature.State);
        Assert.Equal("Microsoft Corporation", signature.Publisher);
    }

    [Fact]
    public void Windows_catalog_signed_file_counts_as_signed() =>
        Assert.Equal(SignatureState.Signed, FileTrust.Check(Path.Combine(Environment.SystemDirectory, "notepad.exe")).State);

    [Fact]
    public void Unsigned_and_tampered_files_are_told_apart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wipriss-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string unsigned = Path.Combine(dir, "plain.exe");
            File.WriteAllBytes(unsigned, new byte[4096]);
            Assert.Equal(SignatureState.Unsigned, FileTrust.Check(unsigned).State);

            string tampered = Path.Combine(dir, "tampered.exe");
            File.Copy(DotnetExe, tampered);
            byte[] bytes = File.ReadAllBytes(tampered);
            bytes[bytes.Length / 3] ^= 0xFF; // inside the signed code, not the signature
            File.WriteAllBytes(tampered, bytes);
            Assert.Equal(SignatureState.Broken, FileTrust.Check(tampered).State);

            Assert.Equal(SignatureState.Unknown, FileTrust.Check(Path.Combine(dir, "missing.exe")).State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class NetworkUsageTests
{
    [Fact]
    public void Windows_usage_reads_without_admin_rights()
    {
        var usage = NetworkUsage.Read(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
        Assert.All(usage, u => Assert.True(u.Received >= 0 && u.Sent >= 0));

        // Desktop programs come as kernel paths; they must map to real files.
        var desktop = usage.Where(u => u.Id.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (desktop.Count > 0)
            Assert.Contains(desktop, u => DevicePaths.ToDosPath(u.Id) is { } path && File.Exists(path));
    }

    [Fact]
    public void Device_paths_only_map_known_volumes()
    {
        Assert.Null(DevicePaths.ToDosPath(@"C:\Windows\notepad.exe"));
        Assert.Null(DevicePaths.ToDosPath(@"\device\nosuchvolume42\x.exe"));
    }

    [Fact]
    public void Internet_connections_leave_out_this_pc()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(IPAddress.Loopback, port);
            Assert.DoesNotContain(PortTable.InternetConnections(), c => c.RemotePort == port && c.Pid == Environment.ProcessId);
        }
        finally
        {
            listener.Stop();
        }
    }
}
