using System.IO;
using Wipriss.Cleaner;

namespace Wipriss.Tests;

/// <summary>Removing programs: which records may be deleted, how uninstall commands are read, the registry copy.</summary>
public class UninstallRulesTests
{
    [Theory]
    [InlineData(@"Software\FooSoft", true)]
    [InlineData(@"Software\FooSoft\Foo Editor", true)]
    [InlineData(@"SOFTWARE\Classes\CLSID\{11111111-2222-3333-4444-555555555555}", true)]
    [InlineData(@"SOFTWARE\Classes\TypeLib\{11111111-2222-3333-4444-555555555555}", true)]
    [InlineData(@"SOFTWARE\Classes\AppID\foo.exe", true)]
    [InlineData(@"SOFTWARE\Classes\FooEditor.Document", true)]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FooEditor", true)]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\foo.exe", true)]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\87654321DCBA10FE32547698BADCFE10", true)]
    [InlineData(@"SOFTWARE\Classes\Installer\Products\87654321DCBA10FE32547698BADCFE10", true)]
    [InlineData(@"Software", false)]
    [InlineData(@"Software\Microsoft", false)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run", false)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", false)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo\Sub", false)]
    [InlineData(@"Software\Classes", false)]
    [InlineData(@"Software\Classes\CLSID", false)]
    [InlineData(@"Software\Classes\CLSID\not-a-guid", false)]
    [InlineData(@"Software\Classes\.txt", false)]
    [InlineData(@"Software\Classes\txtfile", false)]
    [InlineData(@"Software\Classes\Directory", false)]
    [InlineData(@"Software\Classes\Installer\Products\SHORT", false)]
    [InlineData(@"Software\Policies\Foo", false)]
    [InlineData(@"Software\WOW6432Node\Foo", false)]
    [InlineData(@"Software\WindowsApps", false)]
    [InlineData(@"Software\..\Foo", false)]
    [InlineData(@"Software\\Foo", false)]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Foo", false)]
    public void OnlyProgramsOwnKeysMayBeDeleted(string path, bool allowed) =>
        Assert.Equal(allowed, Purge.KeyAllowed(RegRoot.Machine64, path));

    [Fact]
    public void InstallerUserDataIsMachineOnlyAndUserInstallerIsUserOnly()
    {
        const string machine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\87654321DCBA10FE32547698BADCFE10";
        const string user = @"Software\Microsoft\Installer\Products\87654321DCBA10FE32547698BADCFE10";
        Assert.False(Purge.KeyAllowed(RegRoot.User, machine));
        Assert.True(Purge.KeyAllowed(RegRoot.User, user));
        Assert.False(Purge.KeyAllowed(RegRoot.Machine64, user));
    }

    [Theory]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run", "Foo", true)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Foo", true)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run", "", false)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion", "ProgramFilesDir", false)]
    [InlineData(@"Software\FooSoft", "Anything", false)]
    public void OnlyAutostartValuesMayBeDeleted(string key, string name, bool allowed) =>
        Assert.Equal(allowed, Purge.ValueAllowed(key, name));

    [Theory]
    [InlineData(@"\FooUpdate", true)]
    [InlineData(@"\FooSoft\Updater", true)]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", false)]
    [InlineData(@"FooUpdate", false)]
    [InlineData(@"\Foo"" /F & calc", false)]
    [InlineData(@"\", false)]
    public void TasksOfMicrosoftAreNeverDeleted(string path, bool allowed) =>
        Assert.Equal(allowed, Purge.TaskAllowed(path));

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\drivers")]
    [InlineData(@"C:\")]
    [InlineData(@"relative\path")]
    [InlineData("")]
    public void ForbiddenPathsAreRefused(string path) => Assert.False(Purge.PathAllowed(path));

    [Fact]
    public void ProgramFilesItselfIsRefusedButAFolderInsideIsAllowed()
    {
        Assert.False(Purge.PathAllowed(Places.ProgramFiles));
        Assert.True(Purge.PathAllowed(Path.Combine(Places.ProgramFiles, "Wipriss Test Program That Does Not Exist")));
    }

    [Theory]
    [InlineData("{12345678-ABCD-EF01-2345-6789ABCDEF01}", "87654321DCBA10FE32547698BADCFE10")]
    [InlineData("{90160000-008C-0000-1000-0000000FF1CE}", "00006109C80000000100000000F01FEC")]
    public void ProductCodesArePackedAsWindowsInstallerDoes(string code, string packed) =>
        Assert.Equal(packed, Reg.PackGuid(code));

    [Fact]
    public void NotAProductCodeIsNotPacked() => Assert.Null(Reg.PackGuid("FooEditor"));

    [Theory]
    [InlineData("MsiExec.exe /I{12345678-ABCD-EF01-2345-6789ABCDEF01}", "/X{12345678-ABCD-EF01-2345-6789ABCDEF01}")]
    [InlineData(@"""C:\Windows\System32\msiexec.exe"" /X{12345678-ABCD-EF01-2345-6789ABCDEF01}", "/X{12345678-ABCD-EF01-2345-6789ABCDEF01}")]
    public void MsiUninstallAlwaysRemoves(string uninstall, string expectedArguments)
    {
        var command = Uninstall.Command(uninstall, null, null);
        Assert.NotNull(command);
        var parts = Uninstall.Split(command!);
        Assert.NotNull(parts);
        Assert.EndsWith("msiexec.exe", parts!.Value.File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedArguments, parts.Value.Arguments);
    }

    [Theory]
    [InlineData(@"""D:\Apps\Foo Editor\unins000.exe""", @"D:\Apps\Foo Editor\unins000.exe", "")]
    [InlineData(@"""D:\Apps\Foo\uninstall.exe"" /S _?=D:\Apps\Foo", @"D:\Apps\Foo\uninstall.exe", @"/S _?=D:\Apps\Foo")]
    [InlineData(@"D:\Apps\Foo Editor\uninstall.exe --remove", @"D:\Apps\Foo Editor\uninstall.exe", "--remove")]
    [InlineData(@"RunDll32 D:\Apps\Foo\x.dll,Remove", "RunDll32", @"D:\Apps\Foo\x.dll,Remove")]
    public void UninstallCommandsAreSplitIntoFileAndArguments(string command, string file, string arguments)
    {
        var parts = Uninstall.Split(command);
        Assert.Equal(file, parts!.Value.File);
        Assert.Equal(arguments, parts.Value.Arguments);
    }

    [Fact]
    public void InteractiveUninstallIsPreferredOverQuiet() =>
        Assert.Equal(@"D:\Apps\Foo\uninstall.exe", Uninstall.Command(@"D:\Apps\Foo\uninstall.exe", @"D:\Apps\Foo\uninstall.exe /S", null));

    [Theory]
    [InlineData("obsstudio", "obsstudio", true)]
    [InlineData("obsstudioportable", "obsstudio", true)]
    [InlineData("geforceexperience", "nvidiageforceexperience", true)]
    [InlineData("steam", "steamaudio", false)]
    [InlineData("nvidia", "nvidiageforceexperience", false)]
    [InlineData("ab", "ab", false)]
    public void TailNamesMustBeMostOfTheProgramsName(string folder, string program, bool fits) =>
        Assert.Equal(fits, Tails.NameFits(folder, program));

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_11.2502.2.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("Foo.Bar_1.0.0.0_neutral_~_abcdefghijklm", true)]
    [InlineData("Foo'; Remove-Item C:\\ -Recurse; '_1.0_x64__abcdefghijklm", false)]
    [InlineData("NoUnderscores", false)]
    public void StorePackageNamesAreChecked(string name, bool valid) =>
        Assert.Equal(valid, ProgramList.ValidPackageName(name));

    [Fact]
    public void RegistryValuesAreWrittenAsRegeditDoes()
    {
        Assert.Equal("\"Path\"=\"C:\\\\Foo \\\"x\\\"\"", RegFile.FormatValue("Path", 1, System.Text.Encoding.Unicode.GetBytes("C:\\Foo \"x\"\0")));
        Assert.Equal("@=dword:0000000a", RegFile.FormatValue("", 4, BitConverter.GetBytes(10)));
        Assert.Equal("\"Bin\"=hex:01,ff", RegFile.FormatValue("Bin", 3, [1, 255]));
        Assert.Equal("\"Exp\"=hex(2):41,00,00,00", RegFile.FormatValue("Exp", 2, [0x41, 0, 0, 0]));
        Assert.Equal("\"Two\"=hex(1):41,00,0a,00,00,00", RegFile.FormatValue("Two", 1, [0x41, 0, 0x0a, 0, 0, 0])); // a line break stays exact
    }

    [Fact]
    public void LongBinaryValuesWrapLikeRegedit()
    {
        var line = RegFile.FormatValue("Big", 3, new byte[100]);
        Assert.Contains("\\\r\n  ", line);
        Assert.All(line.Split("\r\n"), l => Assert.True(l.Length <= 80));
    }

    [Fact]
    public void OnlyCopiesOfProgramsOwnKeysAreImported()
    {
        const string header = "Windows Registry Editor Version 5.00\r\n";
        Assert.True(RegFile.SafeToImport(header + "\r\n[HKEY_CURRENT_USER\\Software\\FooSoft]\r\n\"A\"=\"b\"\r\n\r\n[HKEY_CURRENT_USER\\Software\\FooSoft\\Sub]\r\n"));
        Assert.True(RegFile.SafeToImport(header + "\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Classes\\CLSID\\{11111111-2222-3333-4444-555555555555}\\InprocServer32]\r\n"));
        Assert.True(RegFile.SafeToImport(header + "\r\n[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n\"Foo\"=\"x\"\r\n"));
        Assert.False(RegFile.SafeToImport(header + "\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon]\r\n\"Shell\"=\"x\"\r\n"));
        Assert.False(RegFile.SafeToImport(header + "\r\n[-HKEY_CURRENT_USER\\Software\\FooSoft]\r\n"));
        Assert.False(RegFile.SafeToImport(header + "\r\n[HKEY_CURRENT_USER\\Software\\FooSoft]\r\n\"A\"=-\r\n"));
        Assert.False(RegFile.SafeToImport(header + "\r\n[HKEY_CLASSES_ROOT\\exefile\\shell\\open\\command]\r\n"));
        Assert.False(RegFile.SafeToImport("REGEDIT4\r\n[HKEY_CURRENT_USER\\Software\\FooSoft]\r\n"));
        // A service may only come back if it is gone, never change one that exists.
        Assert.False(RegFile.SafeToImport(header + "\r\n[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\EventLog]\r\n"));
        Assert.True(RegFile.SafeToImport(header + "\r\n[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WiprissNoSuchService]\r\n"));
    }

    [Fact]
    public void TailIdsBecomeAJobByKind()
    {
        var job = Purge.ToJob(
        [
            new TailItem { Id = @"F|D:\Apps\Foo", Kind = TailKind.Folder },
            new TailItem { Id = @"f|D:\Apps\Foo.lnk", Kind = TailKind.File },
            new TailItem { Id = @"K|HKCU|Software\FooSoft", Kind = TailKind.RegistryKey },
            new TailItem { Id = @"V|HKCU|Software\Microsoft\Windows\CurrentVersion\Run|Foo", Kind = TailKind.RegistryValue },
            new TailItem { Id = "S|FooService", Kind = TailKind.Service },
            new TailItem { Id = @"T|\FooUpdate", Kind = TailKind.Task },
        ]);
        Assert.Equal([@"D:\Apps\Foo", @"D:\Apps\Foo.lnk"], job.Paths);
        Assert.Equal([@"HKCU|Software\FooSoft"], job.Keys);
        Assert.Equal([@"HKCU|Software\Microsoft\Windows\CurrentVersion\Run|Foo"], job.Values);
        Assert.Equal(["FooService"], job.Services);
        Assert.Equal([@"\FooUpdate"], job.Tasks);
    }

    [Fact]
    public void AJobWithRefusedLinesDeletesNothingOfThem()
    {
        var job = new PurgeJob
        {
            Paths = [@"C:\Windows\System32"],
            Keys = [@"HKCU|Software\Microsoft\Windows", @"BAD|Software\Foo"],
            Values = [@"HKCU|Software\FooSoft|Value"],
            Tasks = [@"\Microsoft\Windows\Defrag\ScheduledDefrag"],
        };
        var (freed, failed) = Purge.Run(job, elevated: false, forAdmin: null, CancellationToken.None);
        Assert.Equal(0, freed);
        Assert.Equal(5, failed);
    }
}
