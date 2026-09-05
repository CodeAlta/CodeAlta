param(
    [ValidateSet('Exe', 'WinExe')][string] $OutputType = 'Exe',
    [string] $EvidenceRoot = (Join-Path ([IO.Path]::GetTempPath()) ('codealta-m0-package-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This verification script qualifies Windows only.' }
if (Test-Path $EvidenceRoot) { throw 'EvidenceRoot must be a new probe-only directory.' }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
[void][IO.Directory]::CreateDirectory($EvidenceRoot)
Push-Location $PSScriptRoot
try {
    dotnet pack -c Release --no-restore "-p:OutputType=$OutputType" "-p:OutputPath=$EvidenceRoot/bin/" `
        "-p:IntermediateOutputPath=$EvidenceRoot/obj/" "-p:PublishDir=$EvidenceRoot/publish/" -o "$EvidenceRoot/feed" `
        *> "$EvidenceRoot/pack.log"
    if ($LASTEXITCODE -ne 0) { throw "Pack failed: $EvidenceRoot/pack.log" }
    $package = "$EvidenceRoot/feed/CodeAlta.Desktop.Probe.0.1.0.nupkg"
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
        if ($zip.Entries.FullName -match '(node_modules|\.map$|System.Private.CoreLib.dll)') { throw 'Unexpected runtime/source-map/dependency contents.' }
        $reader = [IO.StreamReader]::new($zip.GetEntry("${prefix}alta-desktop-probe.runtimeconfig.json").Open())
        try { $runtime = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($runtime.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App') { throw 'Not the expected framework-dependent package.' }
        @("Package SHA256=$((Get-FileHash $package).Hash)", "Verified asset hashes=$($manifest.assets.Count)",
          "Asset bytes=$(($manifest.assets | Measure-Object length -Sum).Sum)", 'Native binaries:', $native.FullName) |
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
    dotnet tool install CodeAlta.Desktop.Probe --version 0.1.0 --tool-path "$EvidenceRoot/tools" `
        --configfile "$EvidenceRoot/NuGet.Config" --no-cache *> "$EvidenceRoot/install.log"
    if ($LASTEXITCODE -ne 0) { throw "Local tool install failed: $EvidenceRoot/install.log" }
    foreach ($exe in @("$EvidenceRoot/bin/alta-desktop-probe.exe", "$EvidenceRoot/tools/alta-desktop-probe.exe")) {
        $bytes = [IO.File]::ReadAllBytes($exe)
        $subsystem = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 60) + 92)
        $hasManifest = [Text.Encoding]::UTF8.GetString($bytes).Contains('Microsoft.Windows.Common-Controls')
        if (!$hasManifest -or $subsystem -ne $(if ($OutputType -eq 'Exe') { 3 } else { 2 })) { throw "Unexpected executable manifest/subsystem: $exe" }
        "$exe subsystem=$subsystem CommonControlsManifest=$hasManifest" | Add-Content "$EvidenceRoot/package-inspection.log"
    }
    & ./Verify-Native.ps1 -Executable "$EvidenceRoot/bin/alta-desktop-probe.exe" -EvidenceRoot "$EvidenceRoot/standalone"
    & ./Verify-Native.ps1 -Executable "$EvidenceRoot/tools/alta-desktop-probe.exe" -EvidenceRoot "$EvidenceRoot/installed"
    $loadedNative = Select-String -Path "$EvidenceRoot/installed/smoke.stdout.log" -Pattern '^NATIVE_LIBRARY '
    if (!$loadedNative -or !$loadedNative.Line.Contains("$EvidenceRoot\tools\.store\")) { throw 'Installed tool did not load its own packaged native library.' }
    Get-Content "$EvidenceRoot/package-inspection.log"
    "Package evidence: $EvidenceRoot"
}
finally { Pop-Location }
