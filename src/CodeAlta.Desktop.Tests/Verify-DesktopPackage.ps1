param(
    [ValidateSet('Exe', 'WinExe')][string] $OutputType = 'Exe',
    [string] $EvidenceRoot = (Join-Path ([IO.Path]::GetTempPath()) ('codealta-m1b-package-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This verification script qualifies Windows only.' }
if (Test-Path $EvidenceRoot) { throw 'EvidenceRoot must be a new probe-only directory.' }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
[void][IO.Directory]::CreateDirectory($EvidenceRoot)
Push-Location (Join-Path $PSScriptRoot '../CodeAlta')
try {
    dotnet restore *> "$EvidenceRoot/restore.log"
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $EvidenceRoot/restore.log" }
    dotnet pack -c Release --no-restore -p:MinVerSkip=true -p:Version=0.1.0-m1b "-p:OutputType=$OutputType" "-p:OutputPath=$EvidenceRoot/bin/" `
        "-p:IntermediateOutputPath=$EvidenceRoot/obj/" "-p:PublishDir=$EvidenceRoot/publish/" -o "$EvidenceRoot/feed" `
        *> "$EvidenceRoot/pack.log"
    if ($LASTEXITCODE -ne 0) { throw "Pack failed: $EvidenceRoot/pack.log" }
    $package = "$EvidenceRoot/feed/CodeAlta.0.1.0-m1b.nupkg"
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    $prefix = 'tools/net10.0/any/'
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry("${prefix}assets/neoastra-assets.json").Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        foreach ($asset in $manifest.assets) {
            $entry = $zip.GetEntry("${prefix}assets/$($asset.path)")
            if (!$entry -or $entry.Length -ne $asset.length) { throw "Missing or wrong-sized asset: $($asset.path)" }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($hash -ne $asset.sha256) { throw "Asset hash mismatch: $($asset.path)" }
        }
        $native = @($zip.Entries | Where-Object FullName -Match '/native/(lib)?neoastra_native\.(dll|so|dylib)$')
        if ($native.Count -ne 6 -or ($native.FullName -match 'musl')) { throw 'Expected exactly six non-musl native binaries.' }
        $shims = @($zip.Entries | Where-Object FullName -Match '/shims/[^/]+/alta(\.exe)?$')
        if ($shims.Count -ne 6 -or ($shims.FullName -match 'musl')) { throw 'Expected six standard SDK tool shims.' }
        if ($zip.Entries.FullName -match '(node_modules|\.map$|\.(cs|tsx?|pdb)$|System.Private.CoreLib.dll|CodeAlta\.(Tui|Agent|Plugins|Catalog|Orchestration)|altatui|XenoAtom|Probe)') { throw 'Unexpected sources, fixture, terminal, host or runtime contents.' }
        if ($manifest.assets.Count -ne 3) { throw 'The development boot bundle should contain only HTML, JS and CSS.' }
        $reader = [IO.StreamReader]::new($zip.GetEntry("${prefix}alta.runtimeconfig.json").Open())
        try { $runtime = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($runtime.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App') { throw 'Not the expected framework-dependent package.' }
        foreach ($contract in @('neoastra.manifest.json', 'neoastra.schema.json')) {
            if (!$zip.GetEntry("${prefix}neoastra/$contract")) { throw "Missing runtime contract metadata: $contract" }
        }
        @("Package SHA256=$((Get-FileHash $package).Hash)", "Verified asset hashes=$($manifest.assets.Count)",
          "Asset bytes=$(($manifest.assets | Measure-Object length -Sum).Sum)", "SDK shims=$($shims.Count)",
          'Runtime JSON contracts present; build-only sources/maps/symbols and terminal/host/fixture assemblies absent.',
          'Native binaries:', $native.FullName) |
            Set-Content "$EvidenceRoot/package-inspection.log"
    }
    finally { $zip.Dispose() }

    # Only this generated local feed: no inherited private NuGet sources or global tool installation.
    $writer = [Xml.XmlWriter]::Create("$EvidenceRoot/NuGet.Config")
    try {
        $writer.WriteStartElement('configuration'); $writer.WriteStartElement('packageSources')
        $writer.WriteStartElement('clear'); $writer.WriteEndElement()
        $writer.WriteStartElement('add'); $writer.WriteAttributeString('key', 'probe')
        $writer.WriteAttributeString('value', "$EvidenceRoot/feed"); $writer.WriteEndElement()
        $writer.WriteEndElement(); $writer.WriteEndElement()
    }
    finally { $writer.Dispose() }
    dotnet tool install CodeAlta --version 0.1.0-m1b --tool-path "$EvidenceRoot/tools" `
        --configfile "$EvidenceRoot/NuGet.Config" --no-cache *> "$EvidenceRoot/install.log"
    if ($LASTEXITCODE -ne 0) { throw "Local tool install failed: $EvidenceRoot/install.log" }
    foreach ($exe in @("$EvidenceRoot/bin/alta.exe", "$EvidenceRoot/tools/alta.exe")) {
        $bytes = [IO.File]::ReadAllBytes($exe)
        $subsystem = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 60) + 92)
        $hasManifest = [Text.Encoding]::UTF8.GetString($bytes).Contains('Microsoft.Windows.Common-Controls')
        if (!$hasManifest -or $subsystem -ne $(if ($OutputType -eq 'Exe') { 3 } else { 2 })) { throw "Unexpected executable manifest/subsystem: $exe" }
        "$exe subsystem=$subsystem CommonControlsManifest=$hasManifest" | Add-Content "$EvidenceRoot/package-inspection.log"
    }
    & "$PSScriptRoot/Verify-DesktopNative.ps1" -Executable "$EvidenceRoot/bin/alta.exe" -EvidenceRoot "$EvidenceRoot/standalone"
    & "$PSScriptRoot/Verify-DesktopNative.ps1" -Executable "$EvidenceRoot/tools/alta.exe" -EvidenceRoot "$EvidenceRoot/installed"
    $loadedNative = Select-String -Path "$EvidenceRoot/installed/verification.log" -Pattern '^NATIVE_LIBRARY '
    if (!$loadedNative -or !$loadedNative.Line.Contains("$EvidenceRoot\tools\.store\")) { throw 'Installed tool did not load its own packaged native library.' }
    Get-Content "$EvidenceRoot/package-inspection.log"
    # Test-only fake RPC/channels/visual/dialog/close fixtures are never production methods/assets.
    dotnet restore "$PSScriptRoot/NativeSmoke/CodeAlta.Desktop.Probe.csproj" *> "$EvidenceRoot/fixture-restore.log"
    if ($LASTEXITCODE -ne 0) { throw "Fixture restore failed: $EvidenceRoot/fixture-restore.log" }
    & "$PSScriptRoot/NativeSmoke/Verify-Package.ps1" -EvidenceRoot "$EvidenceRoot/fixture" -OutputType $OutputType
    "Package evidence: $EvidenceRoot"
}
finally { Pop-Location }
