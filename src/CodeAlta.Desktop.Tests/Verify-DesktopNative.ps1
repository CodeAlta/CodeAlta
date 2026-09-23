param(
    [Parameter(Mandatory)][string] $Executable,
    [string] $EvidenceRoot = (Join-Path ([IO.Path]::GetTempPath()) ('codealta-m1b-native-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or ![Environment]::UserInteractive) { throw 'This driver requires an interactive Windows desktop with WebView2.' }
$Executable = (Resolve-Path $Executable).Path
if (Test-Path $EvidenceRoot) { throw 'EvidenceRoot must be a new probe-only directory.' }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
[void][IO.Directory]::CreateDirectory($EvidenceRoot)

function Invoke-Desktop([string] $Name, [string[]] $Arguments, [int] $ExpectedExit, [switch] $Native) {
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
        if ($Native) {
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            do {
                $process.Refresh()
                if ($process.HasExited) { throw "Desktop exited before creating a native window: $($process.ExitCode)" }
                $nativeLibrary = @($process.Modules | Where-Object ModuleName -Match 'neoastra_native')
                if ($process.MainWindowHandle -ne 0 -and $nativeLibrary.Count -eq 1 -and (Test-Path "$EvidenceRoot/profile/webview")) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            if ($process.MainWindowHandle -eq 0 -or $nativeLibrary.Count -ne 1 -or !(Test-Path "$EvidenceRoot/profile/webview")) { throw 'No native desktop window/library/isolated profile before deadline.' }
            # Native boot/close evidence only. The separate fixture proves real bridge/DOM/dialog assertions.
            Start-Sleep -Seconds 3
            $process.Refresh()
            "NATIVE_LIBRARY $($nativeLibrary[0].FileName)" | Add-Content "$EvidenceRoot/verification.log"
            "WINDOW title=$($process.MainWindowTitle) hwnd=$($process.MainWindowHandle)" | Add-Content "$EvidenceRoot/verification.log"
            if ($process.MainWindowTitle -ne 'CodeAlta — in development') { throw 'Unexpected desktop title.' }
            if (!$process.CloseMainWindow()) { throw 'Unable to request native WM_CLOSE on owned desktop process.' }
        }
        if (!$process.WaitForExit(75000)) {
            $process.Kill($true) # Only the process tree created by this invocation.
            $process.WaitForExit()
            throw "Owned desktop $($process.Id) exceeded 75 seconds."
        }
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot "$Name.stdout.log"), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot "$Name.stderr.log"), $stderr.GetAwaiter().GetResult())
        "$Name exit=$($process.ExitCode) expected=$ExpectedExit" | Tee-Object -FilePath (Join-Path $EvidenceRoot 'verification.log') -Append
        if ($process.ExitCode -ne $ExpectedExit) { throw "$Name failed. Evidence: $EvidenceRoot" }
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot "$Name.stdout.log"), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot "$Name.stderr.log"), $stderr.GetAwaiter().GetResult())
        $process.Dispose()
    }
}

"Executable=$Executable`nWorkingDirectory=$EvidenceRoot`nChild PATH=Windows system directories only; NEOASTRA_* and WEBVIEW2_* overrides removed." |
    Set-Content (Join-Path $EvidenceRoot 'verification.log')
Invoke-Desktop 'help' @('--help') 0
Invoke-Desktop 'version' @('--version') 0
# No arguments now start the interactive default owned host and may touch the user's profile.
# Reject a missing explicit value instead; all native runs stay under the test-owned root.
Invoke-Desktop 'missing-root-value' @('--data-root') 2
Invoke-Desktop 'existing-root' @('--data-root', $EvidenceRoot) 2
Invoke-Desktop 'removed-smoke-flag' @('--smoke', '--data-root', (Join-Path $EvidenceRoot 'not-created')) 2
if (Test-Path (Join-Path $EvidenceRoot 'not-created')) { throw 'Rejected flags created storage.' }
Invoke-Desktop 'boot' @('--data-root', (Join-Path $EvidenceRoot 'profile')) 0 -Native
if (!(Select-String -Path (Join-Path $EvidenceRoot 'help.stdout.log') -Pattern 'alta --data-root' -Quiet)) { throw 'Help output was lost.' }
if (!(Select-String -Path (Join-Path $EvidenceRoot 'version.stdout.log') -Pattern '^alta ' -Quiet)) { throw 'Version output was lost.' }
if ((Get-Item (Join-Path $EvidenceRoot 'boot.stderr.log')).Length -ne 0) { throw 'Desktop native startup/cleanup wrote unexpected diagnostics.' }
Get-Content (Join-Path $EvidenceRoot 'verification.log')
"Evidence: $EvidenceRoot"
