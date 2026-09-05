param(
    [Parameter(Mandatory)][string] $Executable,
    [string] $EvidenceRoot = (Join-Path ([IO.Path]::GetTempPath()) ('codealta-m0-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path $Executable).Path
if (Test-Path $EvidenceRoot) { throw 'EvidenceRoot must be a new probe-only directory.' }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
[void][IO.Directory]::CreateDirectory($EvidenceRoot)

function Invoke-Probe([string] $Name, [string[]] $Arguments, [int] $ExpectedExit) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $EvidenceRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    # Child-only changes: no dev server, native override, or alternate WebView runtime/profile.
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match '^(NEOASTRA_|WEBVIEW2_)') { [void]$start.Environment.Remove($key) }
    }
    $start.Environment['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(75000)) {
            $process.Kill($true) # Only the process tree created by this invocation.
            $process.WaitForExit()
            throw "Owned probe $($process.Id) exceeded 75 seconds."
        }
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot "$Name.stdout.log"), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot "$Name.stderr.log"), $stderr.GetAwaiter().GetResult())
        "$Name exit=$($process.ExitCode) expected=$ExpectedExit" | Tee-Object -FilePath (Join-Path $EvidenceRoot 'verification.log') -Append
        if ($process.ExitCode -ne $ExpectedExit) { throw "$Name failed. Evidence: $EvidenceRoot" }
    }
    finally { $process.Dispose() }
}

"Executable=$Executable`nWorkingDirectory=$EvidenceRoot`nChild PATH=Windows system directories only; NEOASTRA_* and WEBVIEW2_* overrides removed." |
    Set-Content (Join-Path $EvidenceRoot 'verification.log')
Invoke-Probe 'help' @('--help') 0
Invoke-Probe 'version' @('--version') 0
Invoke-Probe 'missing-root' @() 2
Invoke-Probe 'existing-root' @('--data-root', $EvidenceRoot) 2
Invoke-Probe 'smoke' @('--smoke', '--data-root', (Join-Path $EvidenceRoot 'profile')) 0
if (!(Select-String -Path (Join-Path $EvidenceRoot 'help.stdout.log') -Pattern 'alta-desktop-probe' -Quiet)) { throw 'Help output was lost.' }
if (!(Select-String -Path (Join-Path $EvidenceRoot 'version.stdout.log') -Pattern '0.1.0' -Quiet)) { throw 'Version output was lost.' }
Get-Content (Join-Path $EvidenceRoot 'smoke.stdout.log')
"Evidence: $EvidenceRoot"
