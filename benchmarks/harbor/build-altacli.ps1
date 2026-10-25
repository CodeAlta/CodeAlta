# Publishes a self-contained altacli for Linux and packs it for the Harbor adapter (locked_codealta.py).
# The sandbox images have no .NET: the runtime is in the package. Invariant globalization: the images may lack libicu.
param(
    [string]$Runtime = "linux-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$dist = Join-Path $PSScriptRoot "dist"
$publish = Join-Path $dist "altacli-$Runtime"
$tarball = Join-Path $dist "altacli-$Runtime.tar.gz"

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish (Join-Path $root "src/CodeAlta.Cli/CodeAlta.Cli.csproj") -c $Configuration -r $Runtime --self-contained `
    -p:InvariantGlobalization=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The native SQLite of SQLitePCLRaw 2.1.13 needs glibc 2.34; many task images have an older one (Debian 11: 2.31).
# The one of 2.1.11 (SQLite 3.49.1) needs glibc 2.28 and works with the managed provider of 2.1.13.
if ($Runtime -eq "linux-x64") {
    $sqliteVersion = "2.1.11"
    $sqliteSha256 = "965400c23fde16a19d000c1c6062af577f048410f85253a793f569d5520daaad"
    $sqliteDir = Join-Path $dist "e_sqlite3-$sqliteVersion"
    if (Test-Path $sqliteDir) { Remove-Item -Recurse -Force $sqliteDir }
    New-Item -ItemType Directory -Force $sqliteDir | Out-Null
    $package = Join-Path $sqliteDir "package.zip"
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/SQLitePCLRaw.lib.e_sqlite3/$sqliteVersion" -OutFile $package
    Expand-Archive -Path $package -DestinationPath (Join-Path $sqliteDir "package")
    $library = Join-Path $sqliteDir "package/runtimes/linux-x64/native/libe_sqlite3.so"
    if ((Get-FileHash -Algorithm SHA256 $library).Hash.ToLowerInvariant() -ne $sqliteSha256) { throw "libe_sqlite3.so of ${sqliteVersion}: unexpected sha256" }
    Copy-Item -Force $library (Join-Path $publish "libe_sqlite3.so")
    Remove-Item -Recurse -Force $sqliteDir
}

if (Test-Path $tarball) { Remove-Item -Force $tarball }
# tar of Windows (bsdtar) keeps no executable bit: the adapter sets it after extracting.
tar -czf $tarball -C $publish .
if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }

$hash = (Get-FileHash -Algorithm SHA256 $tarball).Hash.ToLowerInvariant()
Set-Content -NoNewline -Path "$tarball.sha256" -Value "$hash  altacli-$Runtime.tar.gz`n"
Write-Host "$tarball ($([math]::Round((Get-Item $tarball).Length / 1MB, 1)) MB) sha256 $hash"
