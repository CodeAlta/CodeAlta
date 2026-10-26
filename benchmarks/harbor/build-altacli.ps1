# Publishes glibc and musl self-contained altacli builds and packs them for the Harbor adapter (locked_codealta.py).
# The sandbox images have no .NET: both runtimes are in the package. Invariant globalization: images may lack libicu.
param(
    [string]$Runtime = "linux-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
if ($Runtime -ne "linux-x64") { throw "Only the x64 dual-runtime package is supported; got $Runtime" }
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$dist = Join-Path $PSScriptRoot "dist"
$packageRoot = Join-Path $dist "altacli-$Runtime"
$tarball = Join-Path $dist "altacli-$Runtime.tar.gz"

if (Test-Path $packageRoot) { Remove-Item -Recurse -Force $packageRoot }
New-Item -ItemType Directory -Force $dist | Out-Null

$runtimes = @("linux-x64", "linux-musl-x64")
foreach ($targetRuntime in $runtimes) {
    $publish = Join-Path $packageRoot $targetRuntime
    dotnet publish (Join-Path $root "src/CodeAlta.Cli/CodeAlta.Cli.csproj") -c $Configuration -r $targetRuntime --self-contained `
        -p:InvariantGlobalization=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish for $targetRuntime failed ($LASTEXITCODE)" }
}

# SQLitePCLRaw 2.1.13 needs glibc 2.34. Pin the glibc and musl libraries of 2.1.11 (SQLite 3.49.1);
# its glibc build needs 2.28, and both work with the managed provider of 2.1.13.
$sqliteVersion = "2.1.11"
$sqliteHashes = @{
    "linux-x64" = "965400c23fde16a19d000c1c6062af577f048410f85253a793f569d5520daaad"
    "linux-musl-x64" = "24868846aa49e7d030908d44051a7ecec0e1315524e92bc21a5f6dda816790f6"
}
$sqliteDir = Join-Path $dist "e_sqlite3-$sqliteVersion"
if (Test-Path $sqliteDir) { Remove-Item -Recurse -Force $sqliteDir }
New-Item -ItemType Directory -Force $sqliteDir | Out-Null
$sqlitePackage = Join-Path $sqliteDir "package.zip"
Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/SQLitePCLRaw.lib.e_sqlite3/$sqliteVersion" -OutFile $sqlitePackage
Expand-Archive -Path $sqlitePackage -DestinationPath (Join-Path $sqliteDir "package")
foreach ($targetRuntime in $runtimes) {
    $library = Join-Path $sqliteDir "package/runtimes/$targetRuntime/native/libe_sqlite3.so"
    if ((Get-FileHash -Algorithm SHA256 $library).Hash.ToLowerInvariant() -ne $sqliteHashes[$targetRuntime]) {
        throw "libe_sqlite3.so of $sqliteVersion for ${targetRuntime}: unexpected sha256"
    }
    Copy-Item -Force $library (Join-Path $packageRoot "$targetRuntime/libe_sqlite3.so")
}
Remove-Item -Recurse -Force $sqliteDir

if (Test-Path $tarball) { Remove-Item -Force $tarball }
# tar of Windows (bsdtar) keeps no executable bit: the adapter sets it after extracting.
tar -czf $tarball -C $packageRoot .
if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }

$hash = (Get-FileHash -Algorithm SHA256 $tarball).Hash.ToLowerInvariant()
Set-Content -NoNewline -Path "$tarball.sha256" -Value "$hash  altacli-$Runtime.tar.gz`n"
Write-Host "$tarball ($([math]::Round((Get-Item $tarball).Length / 1MB, 1)) MB) sha256 $hash"
