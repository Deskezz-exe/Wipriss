using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Wipriss.App.Shell;
using Wipriss.Converter;
using Wipriss.ExplorerMenu;
using Wipriss.Modules.Converter;

namespace Wipriss.Tests;

/// <summary>The "Изменить через Wipriss" window and the Explorer menu: layout, settings, saving, safety.</summary>
public sealed class PhotoEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wipriss-edit-test-" + Guid.NewGuid().ToString("N"));

    public PhotoEditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Png(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---- Previews layout ----

    [Fact]
    public void Grid_OnePhotoFillsTheArea()
    {
        var (columns, tile) = PreviewGrid.Fit(1, 800, 600);
        Assert.Equal(1, columns);
        Assert.Equal(800, tile.Width, 1);
        Assert.Equal(600, tile.Height, 1);
    }

    [Fact]
    public void Grid_ThreePhotosAllVisibleAndMedium()
    {
        var (columns, tile) = PreviewGrid.Fit(3, 800, 600);
        int rows = (3 + columns - 1) / columns;
        Assert.True(rows * tile.Height + (rows - 1) * PreviewGrid.Gap <= 600.5, "no scrolling for three");
        Assert.InRange(tile.Width, 200, 400);
    }

    [Fact]
    public void Grid_ManyPhotosGetSmallTilesAndScroll()
    {
        var (fewColumns, few) = PreviewGrid.Fit(4, 800, 600);
        var (columns, many) = PreviewGrid.Fit(60, 800, 600);
        Assert.True(many.Width < few.Width);
        Assert.True(many.Width >= PreviewGrid.MinTile - 0.5);
        int rows = (60 + columns - 1) / columns;
        Assert.True(rows * many.Height > 600, "sixty small tiles need scrolling");
        Assert.True(fewColumns >= 2);
    }

    [Fact]
    public void Grid_EmptyOrZeroWidthIsHarmless()
    {
        Assert.Equal(new Size(0, 0), PreviewGrid.Fit(0, 800, 600).Tile);
        Assert.Equal(new Size(0, 0), PreviewGrid.Fit(5, 0, 600).Tile);
        Assert.True(PreviewGrid.Fit(5, 800, double.PositiveInfinity).Tile.Width > 0);
    }

    // ---- Settings per picture ----

    [Fact]
    public void Settings_ToOptionsAndProblems()
    {
        var s = new PictureSettings { Format = TargetFormat.WebP, LimitWeight = true, LimitMb = 0.5, RemoveBackground = true };
        var options = s.ToOptions();
        Assert.Equal(TargetFormat.WebP, options.Format);
        Assert.Equal(512 * 1024, options.MaxBytes);
        Assert.True(options.RemoveBackground);
        Assert.Null(s.Problem());

        Assert.NotNull(new PictureSettings { Resize = ResizeKind.Pixels }.Problem());
        Assert.NotNull(new PictureSettings { Resize = ResizeKind.Percent, Percent = 0 }.Problem());
        Assert.NotNull(new PictureSettings { LimitWeight = true, LimitMb = 0 }.Problem());
        Assert.Null(new PictureSettings { LimitWeight = false, LimitMb = 0 }.Problem());

        var copy = s.Clone();
        copy.Format = TargetFormat.Png;
        Assert.Equal(TargetFormat.WebP, s.Format);
        Assert.Contains("без фона", s.Short());
    }

    [Fact]
    public void Session_RequestCarriesOwnSettingsAndDrawnCopies()
    {
        var a = Path.Combine(_dir, "a.png");
        var b = Path.Combine(_dir, "b.png");
        File.WriteAllBytes(a, Png(10, 10));
        File.WriteAllBytes(b, Png(10, 10));
        var session = new ConverterSession();
        session.Common.Format = TargetFormat.Jpg;
        Assert.Equal(2, session.Add([a, b, a, "relative.png", Path.Combine(_dir, "missing.png")]));

        session.Items[1].Own = new PictureSettings { Format = TargetFormat.Png, RemoveBackground = true };
        session.Items[1].EditedPath = Path.Combine(_dir, "drawn.png");
        var request = session.BuildRequest(session.Items);

        Assert.Equal(2, request.Jobs.Count);
        Assert.Equal(a, request.Jobs[0].Path);
        Assert.Null(request.Jobs[0].NameFrom);
        Assert.Equal(TargetFormat.Jpg, request.Jobs[0].Options!.Format);
        Assert.Equal(Path.Combine(_dir, "drawn.png"), request.Jobs[1].Path);
        Assert.Equal(b, request.Jobs[1].NameFrom);
        Assert.Equal(TargetFormat.Png, request.Jobs[1].Options!.Format);
        Assert.True(request.Jobs[1].Options!.RemoveBackground);
    }

    [Fact]
    public void Session_ProblemNamesThePicture()
    {
        var a = Path.Combine(_dir, "photo.png");
        File.WriteAllBytes(a, Png(10, 10));
        var session = new ConverterSession();
        session.Add([a]);
        Assert.Null(session.Problem(session.Items));
        session.Items[0].Own = new PictureSettings { Resize = ResizeKind.Pixels };
        Assert.StartsWith("photo.png:", session.Problem(session.Items));
    }

    // ---- The converter with per-file settings ----

    [Fact]
    public void Converter_JobUsesItsOwnSettingsAndTheOriginalsName()
    {
        var original = Path.Combine(_dir, "holiday.jpg");
        File.WriteAllBytes(original, Png(40, 30)); // the content decides, not the extension
        var drawn = Path.Combine(_dir, "temp-drawn.png");
        File.WriteAllBytes(drawn, Png(40, 30));
        var request = new ConvertRequest { Format = TargetFormat.Jpg };
        var job = new ConvertJob { Path = drawn, NameFrom = original, Options = new ConvertOptions { Format = TargetFormat.Png } };

        var reply = ImageConverter.ConvertFile(0, job, request);

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(Path.Combine(_dir, "holiday.png"), reply.Output);
    }

    [Fact]
    public void Converter_OutputFolderKeepsResultsAwayFromOriginals()
    {
        var original = Path.Combine(_dir, "x.png");
        File.WriteAllBytes(original, Png(300, 200));
        var previews = Path.Combine(_dir, "previews");
        var request = new ConvertRequest
        {
            Format = TargetFormat.Png, Resize = ResizeKind.Pixels, Width = 64, Height = 64, OutputFolder = previews, Files = [original],
        };

        var reply = ImageConverter.ConvertFile(0, request.AllJobs()[0], request);

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(previews, Path.GetDirectoryName(reply.Output));
        Assert.Equal(64, reply.Width);
        Assert.Single(Directory.GetFiles(_dir));
    }

    // ---- Replacing originals: nothing is ever lost ----

    [Fact]
    public void Replace_KeepsOriginalWhenResultIsElsewhereOrMissing()
    {
        var original = Path.Combine(_dir, "keep.jpg");
        File.WriteAllBytes(original, [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(_dir, "other"));
        var elsewhere = Path.Combine(_dir, "other", "keep.png");
        File.WriteAllBytes(elsewhere, [4]);

        var (output, note) = OriginalReplacer.Replace(original, elsewhere);
        Assert.Equal(elsewhere, output);
        Assert.NotNull(note);
        Assert.True(File.Exists(original));

        (output, note) = OriginalReplacer.Replace(original, Path.Combine(_dir, "nope.png"));
        Assert.NotNull(note);
        Assert.True(File.Exists(original));

        (_, note) = OriginalReplacer.Replace(original, original);
        Assert.NotNull(note);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(original));

        (_, note) = OriginalReplacer.Replace("keep.jpg", elsewhere);
        Assert.NotNull(note);
    }

    [Fact]
    public void Replace_OnlyWhereThereIsARecycleBin()
    {
        Assert.False(OriginalReplacer.HasRecycleBin(@"\\server\share\photo.jpg"));
        Assert.False(OriginalReplacer.HasRecycleBin("photo.jpg"));
    }

    // ---- Explorer menu ----

    [Fact]
    public void Menu_QuotesPathsForPowerShell()
    {
        Assert.Equal("'D:\\Profile\\Kim''s PC'", MenuPackage.Quote("D:\\Profile\\Kim's PC"));
        Assert.Equal("'a’’b'", MenuPackage.Quote("a’b"));
        Assert.Throws<ArgumentException>(() => MenuPackage.PowerShellArguments("Write-Host \"x\""));
        var script = MenuPackage.AddScript(@"D:\Apps\Wipriss\WiprissExplorerMenu.msix", @"D:\Apps\Wipriss");
        Assert.Contains("-ExternalLocation 'D:\\Apps\\Wipriss'", script);
        Assert.Contains("-AllowUnsigned", script);
        Assert.DoesNotContain("\"", script);
    }

    [Fact]
    public void Menu_ListFileMustBeOursInTemp()
    {
        string temp = Path.GetTempPath();
        Assert.True(ExplorerList.IsOurs(Path.Combine(temp, "Wipriss-convert-1-2-0.txt")));
        Assert.False(ExplorerList.IsOurs(Path.Combine(temp, "other.txt")));
        Assert.False(ExplorerList.IsOurs(Path.Combine(temp, "sub", "Wipriss-convert-1.txt")));
        Assert.False(ExplorerList.IsOurs(@"D:\Profile\Wipriss-convert-1.txt"));
        Assert.False(ExplorerList.IsOurs("Wipriss-convert-1.txt"));

        var list = Path.Combine(temp, $"Wipriss-convert-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(list, "D:\\Profile\\a.png\r\nrelative.png\r\n\r\nD:\\Profile\\b.jpg\r\n");
        Assert.Equal([@"D:\Profile\a.png", @"D:\Profile\b.jpg"], ExplorerList.Read(list));
        Assert.False(File.Exists(list));
    }

    /// <summary>The C DLL, the package manifest and the C# side must agree on the CLSID, names and title file.</summary>
    [Fact]
    public void Menu_DllManifestAndAppAgree()
    {
        string folder = Path.Combine(RepoRoot(), "src", "Wipriss.ExplorerMenu");
        var manifest = XDocument.Load(Path.Combine(folder, "AppxManifest.xml"));
        XNamespace f = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = manifest.Root!.Element(f + "Identity")!;
        Assert.Equal(MenuPackage.PackageName, identity.Attribute("Name")!.Value);
        Assert.Equal(MenuPackage.Publisher, identity.Attribute("Publisher")!.Value);

        Assert.Equal(MenuPackage.Version, identity.Attribute("Version")!.Value);
        Assert.Equal(System.Version.Parse(MenuPackage.Version), MenuEntry.Tables.MinPackage);
        Assert.Equal(System.Version.Parse("1.1.0.0"), MenuPackage.VersionOf("Wipriss.ExplorerMenu_1.1.0.0_x64__abcdef"));

        // Each verb points at a class the surrogate serves from the DLL, and the C code knows both classes.
        var classes = manifest.Descendants().Where(e => e.Name.LocalName == "Class").ToList();
        Assert.Equal(2, classes.Count);
        Assert.All(classes, e => Assert.Equal(MenuPackage.DllFileName, e.Attribute("Path")!.Value));
        string c = File.ReadAllText(Path.Combine(folder, "WiprissExplorerMenu.c"));
        foreach (var (verb, field) in new[] { ("WiprissEdit", "kClsidCommand"), ("WiprissTable", "kClsidTables") })
        {
            var clsid = manifest.Descendants().Where(e => e.Name.LocalName == "Verb" && e.Attribute("Id")!.Value == verb)
                .Select(e => e.Attribute("Clsid")!.Value).Distinct().Single();
            Assert.Contains(classes, e => e.Attribute("Id")!.Value == clsid);
            Assert.Contains("// {" + clsid + "}", c);
            string data1 = BitConverter.ToUInt32(Guid.Parse(clsid).ToByteArray(), 0).ToString("x8");
            Assert.Matches(new Regex(field + @" = \{0x" + data1), c);
        }
        Assert.Contains("L\"" + MenuPackage.ListFilePrefix + "\"", c);
        Assert.Contains("#define TITLE_FILE L\"" + MenuPackage.TitleFileName + "\"", c);
        Assert.Contains("#define TABLES_TITLE_FILE L\"" + MenuPackage.TablesTitleFileName + "\"", c);
        Assert.Contains("#define READER_EXE L\"" + MenuPackage.ReaderExeName + "\"", c);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, MenuPackage.TitleFileName), MenuPackage.TitleFilePath);

        // Pictures: every type of the classic menu is in the new one too.
        HashSet<string> TypesOf(string verb) => manifest.Descendants().Where(e => e.Name.LocalName == "ItemType"
                && e.Elements().Any(v => v.Attribute("Id")?.Value == verb))
            .Select(e => e.Attribute("Type")!.Value).ToHashSet();
        Assert.True(TypesOf("WiprissEdit").SetEquals(MenuEntry.Convert.Extensions));
        // Tables: registered for every file like Filee (Windows 11 skips some single types); the DLL checks the
        // same types as the app.
        Assert.Equal(["*"], TypesOf("WiprissTable"));
        var dllTypes = Regex.Match(c, @"kTableTypes\[\] = \{([^}]*)\}").Groups[1].Value
            .Split(',').Select(s => s.Trim().TrimStart('L').Trim('"')).ToHashSet();
        Assert.True(dllTypes.SetEquals(MenuEntry.Tables.Extensions));
    }

    private static string RepoRoot()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
            if (File.Exists(Path.Combine(at, "Wipriss.slnx"))) return at;
        throw new InvalidOperationException("repository not found");
    }
}

public sealed class ReplaceFollowTests
{
    [Fact]
    public void Row_FollowsTheNewFileAfterReplace()
    {
        var row = new FileRow(@"D:\Profile\a.jpg");
        row.Rebase(@"D:\Profile\a.png");
        Assert.Equal(@"D:\Profile\a.png", row.Path);
        Assert.Equal("a.png", row.Name);
    }
}
