// Packs WiprissExplorerMenu.msix, the unsigned sparse package behind "Изменить через Wipriss" in the Windows 11
// Explorer menu. Run by tools/build-explorer-menu.ps1:
//   dotnet run tools/make-explorer-package.cs -- <manifest> <icon.ico> <output.msix>
// Uses the Windows packaging API (AppxPackaging.dll, part of Windows 10/11): it validates the manifest and writes the
// block map and content types like MakeAppx, so no Windows SDK is needed.
//
// Based on build/tools/make-explorer-package.cs from Filee (https://github.com/KnifeLemon/Filee, commit a89b139),
// Copyright (c) 2026 Myeonggu Kim (KnifeLemon) and Filee contributors, MIT License (see THIRD-PARTY-NOTICES.md).
// Changes for Wipriss: takes a ready manifest file instead of generating it; the logo is the 256 px PNG frame of
// the app icon; no architecture switch.
#:property AllowUnsafeBlocks=true
#:property PublishAot=false

using System.Runtime.InteropServices;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: dotnet run tools/make-explorer-package.cs -- <AppxManifest.xml> <icon.ico> <output.msix>");
    return 2;
}
var manifest = Path.GetFullPath(args[0]);
var icon = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.Delete(output);

var work = Directory.CreateTempSubdirectory("wipriss-msix-");
try
{
    var logo = Path.Combine(work.FullName, "Logo.png");
    File.WriteAllBytes(logo, BiggestPngFrame(File.ReadAllBytes(icon)));
    Appx.Pack(output, manifest, [(@"Assets\Logo.png", logo, "image/png")]);
    Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length:N0} bytes)");
}
finally
{
    work.Delete(recursive: true);
}
return 0;

static byte[] BiggestPngFrame(byte[] ico)
{
    int count = BitConverter.ToUInt16(ico, 4), best = -1, bestSize = 0;
    for (int i = 0; i < count; i++)
    {
        int entry = 6 + 16 * i, side = ico[entry] == 0 ? 256 : ico[entry];
        int offset = (int)BitConverter.ToUInt32(ico, entry + 12);
        bool png = ico[offset] == 0x89 && ico[offset + 1] == (byte)'P';
        if (png && side > bestSize) { best = i; bestSize = side; }
    }
    if (best < 0) throw new InvalidOperationException("the icon has no PNG frame");
    int at = 6 + 16 * best;
    int length = (int)BitConverter.ToUInt32(ico, at + 8), start = (int)BitConverter.ToUInt32(ico, at + 12);
    return ico.AsSpan(start, length).ToArray();
}

/// <summary>Minimal raw-vtable client of IAppxFactory / IAppxPackageWriter (no COM interop marshalling needed).</summary>
internal static unsafe class Appx
{
    private static readonly Guid ClsidAppxFactory = new("5842a140-ff9f-4166-8f5c-62f5b7b0c781");
    private static readonly Guid IidAppxFactory = new("beb94909-e451-438b-b5a7-d79e767b75d8");
    private const uint ClsctxInprocServer = 1;
    private const uint CoinitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint StgmRead = 0, StgmWrite = 1, StgmShareDenyWrite = 0x20, StgmShareExclusive = 0x10, StgmCreate = 0x1000;
    private const int AppxCompressionNormal = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PackageSettings
    {
        public int ForceZip32;
        public nint HashMethod; // IUri*
    }

    public static void Pack(string packagePath, string manifestPath, IEnumerable<(string Name, string Path, string Type)> payload)
    {
        var init = CoInitializeEx(0, CoinitMultithreaded);
        if (init != RpcEChangedMode) Check(init, "CoInitializeEx");
        nint factory = 0, output = 0, hash = 0, writer = 0;
        try
        {
            var clsid = ClsidAppxFactory;
            var iid = IidAppxFactory;
            Check(CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, out factory), "CoCreateInstance(AppxFactory)");
            Check(SHCreateStreamOnFileEx(packagePath, StgmCreate | StgmWrite | StgmShareExclusive, 0x80, 1, 0, out output), "create " + packagePath);
            Check(CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 0, 0, out hash), "CreateUri");
            var settings = new PackageSettings { ForceZip32 = 1, HashMethod = hash };
            // IAppxFactory::CreatePackageWriter(IStream*, APPX_PACKAGE_SETTINGS*, IAppxPackageWriter**) is slot 3.
            var create = (delegate* unmanaged[Stdcall]<nint, nint, PackageSettings*, nint*, int>)Slot(factory, 3);
            nint created;
            Check(create(factory, output, &settings, &created), "CreatePackageWriter");
            writer = created;

            foreach (var (name, path, type) in payload)
            {
                Check(SHCreateStreamOnFileEx(path, StgmRead | StgmShareDenyWrite, 0, 0, 0, out var input), "open " + path);
                try
                {
                    // IAppxPackageWriter::AddPayloadFile(LPCWSTR, LPCWSTR, APPX_COMPRESSION_OPTION, IStream*) is slot 3.
                    var add = (delegate* unmanaged[Stdcall]<nint, char*, char*, int, nint, int>)Slot(writer, 3);
                    fixed (char* n = name)
                    fixed (char* t = type)
                        Check(add(writer, n, t, AppxCompressionNormal, input), "AddPayloadFile " + name);
                }
                finally
                {
                    Release(input);
                }
            }

            Check(SHCreateStreamOnFileEx(manifestPath, StgmRead | StgmShareDenyWrite, 0, 0, 0, out var manifest), "open manifest");
            try
            {
                // IAppxPackageWriter::Close(IStream* manifest) is slot 4; it validates the manifest against the schema.
                var close = (delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(writer, 4);
                Check(close(writer, manifest), "Close (manifest validation)");
            }
            finally
            {
                Release(manifest);
            }
        }
        finally
        {
            Release(writer);
            Release(hash);
            Release(output);
            Release(factory);
        }
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Release(nint instance)
    {
        if (instance != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed: 0x{hr:X8} {Marshal.GetExceptionForHR(hr)?.Message}");
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, int create, nint template, out nint stream);

    [DllImport("urlmon.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateUri(string uri, uint flags, nuint reserved, out nint result);
}
