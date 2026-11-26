<#
.SYNOPSIS
  Builds the Windows 11 Explorer menu: WiprissExplorerMenu.dll + WiprissExplorerMenu.msix, next to Wipriss.exe.

.DESCRIPTION
  The DLL (src/Wipriss.ExplorerMenu, plain C, no C runtime) is compiled with a pinned, portable Zig toolchain that is
  downloaded once into artifacts/.cache and checked by SHA-256, so neither Visual C++ nor the Windows SDK is needed.
  The unsigned sparse package is packed by tools/make-explorer-package.cs through Windows' own packaging API.

  Based on build/build-explorer-menu.ps1 from Filee (https://github.com/KnifeLemon/Filee), Copyright (c) 2026
  Myeonggu Kim (KnifeLemon) and Filee contributors, MIT License. Changes: Wipriss paths and names, x64 only,
  Windows PowerShell 5.1 compatible, no retry loop.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build-explorer-menu.ps1
  Output: artifacts\.cache\explorer-menu; `dotnet publish src\Wipriss.App` then copies both files next to Wipriss.exe.
#>
[CmdletBinding()]
param(
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cache = Join-Path $root 'artifacts\.cache'
if (-not $Destination) { $Destination = Join-Path $cache 'explorer-menu' }

# Zig 0.16.0 for x86_64 Windows. Update the version, hash and size together.
$zigName = 'zig-x86_64-windows-0.16.0'
$zigSha256 = '68659EB5F1E4EB1437A722F1DD889C5A322C9954607F5EDCF337BC3684A75A7E'
$zigUrls = @(
    "https://ziglang.freetls.fastly.net/$zigName.zip",
    "https://pkg.hexops.org/zig/$zigName.zip",
    "https://ziglang.org/download/0.16.0/$zigName.zip"
)

function Get-Zig {
    $folder = Join-Path $cache $zigName
    $exe = Join-Path $folder 'zig.exe'
    if (Test-Path $exe) { return $exe }

    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $zip = Join-Path $cache "$zigName.zip"
    $ok = (Test-Path $zip) -and (Get-FileHash $zip -Algorithm SHA256).Hash -eq $zigSha256
    foreach ($url in $zigUrls) {
        if ($ok) { break }
        Write-Host "get  $url"
        & curl.exe --fail --location --silent --show-error --connect-timeout 20 --speed-limit 200000 --speed-time 30 `
            --max-time 600 --output $zip $url
        if ($LASTEXITCODE -eq 0 -and (Get-FileHash $zip -Algorithm SHA256).Hash -eq $zigSha256) { $ok = $true }
        else { Remove-Item $zip -ErrorAction SilentlyContinue }
    }
    if (-not $ok) { throw "Could not download $zigName." }

    Write-Host "unpack $zigName"
    $partial = Join-Path $cache "$zigName.partial"
    Remove-Item $partial -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $partial | Out-Null
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $zip -C $partial
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    Move-Item (Join-Path $partial $zigName) $folder
    Remove-Item $partial -Recurse -Force
    Remove-Item $zip
    return $exe
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$parts = (($version -split '[-+]')[0].Split('.') + @('0', '0', '0'))
$major, $minor, $patch = [int]$parts[0], [int]$parts[1], [int]$parts[2]

$zig = Get-Zig
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $cache 'zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $cache 'zig-cache'
$source = Join-Path $root 'src\Wipriss.ExplorerMenu'
$obj = Join-Path $cache 'explorer-menu-obj'
New-Item -ItemType Directory -Force -Path $obj, $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

# The version reaches the resource through a generated prefix (zig's resource compiler has no -D).
$rc = Join-Path $obj 'WiprissExplorerMenu.rc'
$defines = "#define WIPRISS_VERSION_MAJOR $major`r`n#define WIPRISS_VERSION_MINOR $minor`r`n#define WIPRISS_VERSION_PATCH $patch`r`n#define WIPRISS_VERSION_TEXT `"$version`"`r`n"
[IO.File]::WriteAllText($rc, $defines + [IO.File]::ReadAllText((Join-Path $source 'WiprissExplorerMenu.rc')), (New-Object Text.UTF8Encoding $false))

Write-Host "build WiprissExplorerMenu.dll $version"
$dll = Join-Path $obj 'WiprissExplorerMenu.dll'
# -nostdlib: no C runtime inside explorer.exe; the entry point is DllMain itself. -s strips symbols.
& $zig cc -target x86_64-windows-gnu -shared -O2 -s -nostdlib -Wall -Wextra -Werror `
    -isystem (Join-Path (Split-Path $zig) 'lib\libc\include\any-windows-any') `
    (Join-Path $source 'WiprissExplorerMenu.c') (Join-Path $source 'WiprissExplorerMenu.def') $rc `
    -lkernel32 -luser32 -lole32 -lshell32 -lshlwapi '-Wl,--entry=DllMain' -o $dll
if ($LASTEXITCODE -ne 0) { throw "zig cc failed with exit code $LASTEXITCODE" }
Copy-Item $dll $Destination -Force

Write-Host "pack WiprissExplorerMenu.msix"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& dotnet run (Join-Path $root 'tools\make-explorer-package.cs') -- (Join-Path $source 'AppxManifest.xml') `
    (Join-Path $root 'src\Wipriss.App\Assets\wipriss.ico') (Join-Path $Destination 'WiprissExplorerMenu.msix')
if ($LASTEXITCODE -ne 0) { throw "make-explorer-package failed with exit code $LASTEXITCODE" }

Write-Host "Explorer menu ready in $Destination"
