using System.Diagnostics;
using System.IO;
using System.Text;
using Wipriss.Cleaner;

namespace Wipriss.Tests;

public class CleanerRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0);

    private static LeftoverFacts Facts(LeftoverPlace place = LeftoverPlace.Roaming, int daysAgo = 200, int personal = 0,
        bool saves = false, string[]? programs = null, bool denied = false, int files = 10) =>
        new(place, Now.AddDays(-daysAgo), personal, saves, programs ?? [], denied, files * 1000L, files, Now);

    [Fact]
    public void OldLeftoverWithoutPersonalFilesIsGreen()
    {
        var (color, confidence, evidence) = Verdict.Leftover(Facts());
        Assert.Equal(CleanColor.Green, color);
        Assert.Equal("высокая", confidence);
        Assert.Contains(evidence, e => e.Contains("Нет среди установленных"));
    }

    [Theory]
    [InlineData(5, 0, false)]   // changed recently
    [InlineData(200, 3, false)] // documents, photos or video inside
    [InlineData(200, 0, true)]  // looks like game saves
    public void DoubtfulLeftoverIsYellow(int daysAgo, int personal, bool saves) =>
        Assert.Equal(CleanColor.Yellow, Verdict.Leftover(Facts(daysAgo: daysAgo, personal: personal, saves: saves)).Color);

    [Fact]
    public void FolderInDriveRootIsNeverGreen() =>
        Assert.Equal(CleanColor.Yellow, Verdict.Leftover(Facts(LeftoverPlace.DriveRoot)).Color);

    [Fact]
    public void LocalLowIsNeverGreenBecauseOfGameSaves() =>
        Assert.Equal(CleanColor.Yellow, Verdict.Leftover(Facts(LeftoverPlace.LocalLow)).Color);

    [Fact]
    public void ProgramDataWithFilesIsYellowButEmptyIsGreen()
    {
        Assert.Equal(CleanColor.Yellow, Verdict.Leftover(Facts(LeftoverPlace.ProgramData)).Color);
        Assert.Equal(CleanColor.Green, Verdict.Leftover(Facts(LeftoverPlace.ProgramData, files: 0)).Color);
    }

    [Fact]
    public void FolderThatCouldNotBeReadIsYellow() =>
        Assert.Equal(CleanColor.Yellow, Verdict.Leftover(Facts(denied: true)).Color);

    [Fact]
    public void ProgramOutsideProgramFilesMayBePortable()
    {
        Assert.Equal(CleanColor.Yellow, Verdict.Leftover(Facts(LeftoverPlace.Local, programs: [@"D:\x\tool.exe"])).Color);
        Assert.Equal(CleanColor.Green, Verdict.Leftover(Facts(LeftoverPlace.ProgramFiles, programs: [@"D:\x\tool.exe"])).Color);
    }

    [Fact]
    public void WindowsAndItsStoresAreProtected()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(Verdict.IsProtected(win));
        Assert.True(Verdict.IsProtected(Path.Combine(win, "System32")));
        Assert.True(Verdict.IsProtected(Path.Combine(win, "WinSxS")));
        Assert.True(Verdict.IsProtected(Path.Combine(win, "Installer")));
        Assert.True(Verdict.IsProtected(Path.Combine(data, "Package Cache", "{abc}")));
        Assert.True(Verdict.IsProtected(Path.Combine(data, "Microsoft")));
        Assert.True(Verdict.IsProtected(Path.Combine(programs, "WindowsApps")));
        Assert.True(Verdict.IsProtected(Path.Combine(programs, "Common Files", "x")));
        Assert.True(Verdict.IsProtected(Path.GetPathRoot(win)!));
        Assert.True(Verdict.IsProtected("relative\\path"));
        Assert.False(Verdict.IsProtected(Path.Combine(programs, "Some Old Program")));
        // «Windows Something» next to Windows is not inside it
        Assert.False(Verdict.IsProtected(win + "Backup"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --start", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Program Files\\App Dir\\app.exe -k netsvcs", "C:\\Program Files\\App Dir\\app.exe")]
    [InlineData("\\??\\C:\\Drivers\\x.sys", "C:\\Drivers\\x.sys")]
    [InlineData("rundll32.exe \"C:\\Tools\\lib.dll\",Entry", "C:\\Tools\\lib.dll")]
    [InlineData("C:\\Windows\\System32\\rundll32.exe C:\\Tools\\lib.dll,Entry", "C:\\Tools\\lib.dll")]
    [InlineData("svchost.exe -k LocalService", null)]
    [InlineData("", null)]
    public void ProgramIsTakenFromCommandLine(string command, string? expected) =>
        Assert.Equal(expected, PathText.ExePath(command));

    [Theory]
    [InlineData("Mozilla Firefox (x64 ru)", "mozillafirefox")]
    [InlineData("Notepad++ 8.6.2", "notepad")]
    [InlineData("Valve Corporation", "valve")]
    [InlineData("7-Zip 23.01 (x64)", "7zip")]
    public void NamesAreNormalized(string name, string key) => Assert.Equal(key, PathText.NameKey(name));

    [Theory]
    [InlineData("mozilla", "mozillafirefox", true)]
    [InlineData("steamlibrary", "steam", true)]
    [InlineData("app", "myappstudio", false)]     // short names only match exactly
    [InlineData("zoom", "zoomtext", true)]
    [InlineData("abc", "abc", true)]
    [InlineData("", "abc", false)]
    public void NamesMatchByContaining(string folder, string other, bool expected) =>
        Assert.Equal(expected, PathText.NamesMatch(folder, other));

    [Fact]
    public void InsideComparesWholeFolderNames()
    {
        Assert.True(PathText.IsInside(@"D:\Apps\Foo\bin", @"D:\Apps\Foo"));
        Assert.True(PathText.IsInside(@"d:\apps\foo", @"D:\Apps\Foo"));
        Assert.False(PathText.IsInside(@"D:\Apps\FooBar", @"D:\Apps\Foo"));
    }

    [Theory]
    [InlineData("Tobii Interaction", true)]
    [InlineData("PyCharm2021.3", true)]
    [InlineData("plugins", false)]
    [InlineData("Statistics", false)]
    [InlineData("ComputeCache", false)]
    [InlineData("BT_backup", false)]
    [InlineData("Common", false)]
    public void VendorSubfoldersMustLookLikeProducts(string name, bool expected) =>
        Assert.Equal(expected, Leftovers.LooksLikeProduct(name));

    [Fact]
    public void ShortcutTargetIsReadFromLinkInfo()
    {
        const string target = @"D:\Apps\Тест\app.exe";
        Assert.Equal(target, PathText.ShortcutTarget(Shortcut(target)));
        Assert.Null(PathText.ShortcutTarget([1, 2, 3]));
        Assert.Null(PathText.ShortcutTarget(new byte[0x4C]));
    }

    /// <summary>A minimal .lnk (MS-SHLLINK): header, no ID list, LinkInfo with a Unicode local base path.</summary>
    private static byte[] Shortcut(string target)
    {
        var header = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(header, 0);
        BitConverter.GetBytes(0x2 | 0x80).CopyTo(header, 0x14); // HasLinkInfo | IsUnicode
        var unicode = Encoding.Unicode.GetBytes(target + "\0");
        var ansi = Encoding.ASCII.GetBytes("x\0");
        const int infoHeader = 0x24;
        int ansiOffset = infoHeader, unicodeOffset = infoHeader + ansi.Length;
        var info = new byte[infoHeader + ansi.Length + unicode.Length];
        BitConverter.GetBytes(info.Length).CopyTo(info, 0);
        BitConverter.GetBytes(infoHeader).CopyTo(info, 4);
        BitConverter.GetBytes(1).CopyTo(info, 8); // VolumeIDAndLocalBasePath
        BitConverter.GetBytes(ansiOffset).CopyTo(info, 0x10);
        BitConverter.GetBytes(unicodeOffset).CopyTo(info, 0x1C);
        ansi.CopyTo(info, ansiOffset);
        unicode.CopyTo(info, unicodeOffset);
        return [.. header, .. info];
    }

    [Fact]
    public void HiddenPathsAreDroppedWithEverythingUnderThem()
    {
        var group = new CleanGroup
        {
            Title = "X",
            Items =
            [
                new CleanItem { Title = "a", Paths = [@"D:\Data\Foo\cache"] },
                new CleanItem { Title = "b", Paths = [@"D:\Data\Bar"] },
            ],
        };
        var left = Scanner.WithoutHidden(group, [@"D:\Data\Foo"]);
        Assert.NotNull(left);
        Assert.Equal("b", Assert.Single(left!.Items).Title);
        Assert.Null(Scanner.WithoutHidden(left, [@"D:\Data"]));
    }

    [Fact]
    public void FolderStatsCountsPersonalFilesAndSaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "wipriss-cleaner-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Saves"));
            File.WriteAllBytes(Path.Combine(root, "photo.jpg"), new byte[100]);
            File.WriteAllBytes(Path.Combine(root, "Saves", "slot1.dat"), new byte[50]);
            File.WriteAllBytes(Path.Combine(root, "tool.exe"), new byte[10]);
            var stats = FolderStats.Measure(root);
            Assert.True(stats.Exists);
            Assert.Equal(160, stats.Size);
            Assert.Equal(3, stats.Files);
            Assert.Equal(1, stats.PersonalFiles);
            Assert.True(stats.LooksLikeSaves);
            Assert.Single(stats.Programs);
            Assert.False(FolderStats.Measure(Path.Combine(root, "missing")).Exists);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public class CleanerDeleteTests
{
    private static string NewFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "wipriss-delete-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string OldFile(string path, int bytes = 10)
    {
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        return path;
    }

    [Fact]
    public void MainFoldersAreNeverDeleted()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(Deleter.IsForbidden(profile));
        Assert.True(Deleter.IsForbidden(Path.Combine(profile, "Downloads")));
        Assert.True(Deleter.IsForbidden(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert.True(Deleter.IsForbidden(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Assert.True(Deleter.IsForbidden(programs));
        Assert.True(Deleter.IsForbidden(windows));
        Assert.True(Deleter.IsForbidden(Path.Combine(windows, "System32", "drivers")));
        Assert.True(Deleter.IsForbidden(Path.Combine(windows, "Fonts")));
        Assert.True(Deleter.IsForbidden(Path.GetPathRoot(windows)!));
        Assert.True(Deleter.IsForbidden(Path.Combine(profile, "Wipriss", "Служебное")));
        Assert.True(Deleter.IsForbidden(Path.Combine(AppContext.BaseDirectory, "x.dll")));
        Assert.True(Deleter.IsForbidden("relative"));
        Assert.False(Deleter.IsForbidden(Path.Combine(windows, "Temp", "x.tmp")));
        Assert.False(Deleter.IsForbidden(Path.Combine(programs, "Old Program")));
    }

    [Fact]
    public void RedAndWindowsOnlyItemsAreNotDeleted()
    {
        var root = NewFolder();
        try
        {
            var file = OldFile(Path.Combine(root, "a.bin"));
            var red = new CleanItem { Paths = [file], Color = CleanColor.Red, Action = CleanAction.Whole };
            var windowsOnly = new CleanItem { Paths = [file], Color = CleanColor.Green, Action = CleanAction.WindowsOnly };
            Deleter.Delete([red, windowsOnly], _ => { }, CancellationToken.None);
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ContentsKeepTheFolderAndFreshFiles()
    {
        var root = NewFolder();
        try
        {
            var cache = Directory.CreateDirectory(Path.Combine(root, "Cache")).FullName;
            Directory.CreateDirectory(Path.Combine(cache, "sub"));
            OldFile(Path.Combine(cache, "old.bin"), 100);
            OldFile(Path.Combine(cache, "sub", "old2.bin"), 50);
            var fresh = Path.Combine(cache, "in-use.bin");
            File.WriteAllBytes(fresh, new byte[7]);
            var item = new CleanItem { Paths = [cache], Color = CleanColor.Green, Action = CleanAction.Contents };
            var (freed, failed) = Deleter.Delete([item], _ => { }, CancellationToken.None);
            Assert.Equal(150, freed);
            Assert.Equal(0, failed);
            Assert.True(Directory.Exists(cache));
            Assert.True(File.Exists(fresh));
            Assert.False(Directory.Exists(Path.Combine(cache, "sub")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WholeRemovesTheFolderButNeverFollowsJunctions()
    {
        var root = NewFolder();
        try
        {
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var precious = OldFile(Path.Combine(outside, "precious.doc"));
            var leftover = Directory.CreateDirectory(Path.Combine(root, "leftover")).FullName;
            OldFile(Path.Combine(leftover, "junk.bin"));
            var link = Path.Combine(leftover, "link");
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false });
            mklink!.WaitForExit();
            Assert.True(Directory.Exists(link));

            var item = new CleanItem { Paths = [leftover], Color = CleanColor.Yellow, Action = CleanAction.Whole };
            Deleter.Delete([item], _ => { }, CancellationToken.None);

            Assert.True(File.Exists(precious));                    // the junction's target is untouched
            Assert.False(File.Exists(Path.Combine(leftover, "junk.bin")));
            Directory.Delete(link);                                // removes only the link
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ALinkGivenAsThePathIsNotFollowed()
    {
        var root = NewFolder();
        try
        {
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var precious = OldFile(Path.Combine(outside, "precious.doc"));
            var link = Path.Combine(root, "link");
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false });
            mklink!.WaitForExit();
            var item = new CleanItem { Paths = [link], Color = CleanColor.Green, Action = CleanAction.Whole };
            Deleter.Delete([item], _ => { }, CancellationToken.None);
            Assert.True(File.Exists(precious));
            Directory.Delete(link);
        }
        finally { Directory.Delete(root, true); }
    }
}
