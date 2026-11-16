<#
CodeAlta installer for Windows: https://codealta.io

    irm https://codealta.io/install.ps1 | iex

It installs what CodeAlta needs and CodeAlta itself, for the current user:
  1. the .NET 10 SDK, when no suitable one is found: in %LOCALAPPDATA%\Microsoft\dotnet with Microsoft's dotnet-install
     script, without administrator rights. A .NET of the system that is too old is updated where it is, with winget or
     the installer of Microsoft, for which Windows asks your permission;
  2. the WebView2 runtime, which the window of the desktop app uses, on a Windows that lacks it;
  3. CodeAlta, a .NET tool of nuget.org (dotnet tool update -g CodeAlta);
  4. %USERPROFILE%\.dotnet\tools on your PATH, when it is not there yet.
A .NET 10 that is already there, of the system or of the user, is used as it is. Run the script again to update CodeAlta.

Options, for example for the terminal app:

    & ([scriptblock]::Create((irm https://codealta.io/install.ps1))) -Tui

  -Desktop       CodeAlta Desktop (alta): the default
  -Tui           CodeAlta TUI (altatui), the terminal app
  -Cli           CodeAlta CLI (altacli), for scripts, CI and editors
  -All           the three of them
  -Prerelease    the latest preview instead of the latest release
  -Version <v>   that version of CodeAlta
  -NoModifyPath  leave the PATH and DOTNET_ROOT of your account as they are

macOS and Linux: curl -fsSL https://codealta.io/install.sh | sh

This file is ASCII only: Windows PowerShell reads a downloaded script in the encoding of the system. It runs in the
session that downloaded it, so it defines nothing there: everything, its parameters included, is inside one script
block, which gets the arguments of the script. It does not end that session: only a run from a file ends with an exit code.
#>
& {
    param(
        [switch] $Desktop,
        [switch] $Tui,
        [switch] $Cli,
        [switch] $All,
        [switch] $Prerelease,
        [string] $Version,
        [switch] $NoModifyPath
    )

    $Options = @{ Desktop = $Desktop; Tui = $Tui; Cli = $Cli; All = $All; Prerelease = $Prerelease; Version = $Version; NoModifyPath = $NoModifyPath }
    # The path of this script when it runs from a file (powershell -File install.ps1); nothing when a session runs its text.
    $FromFile = [bool]$PSCommandPath

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'

    # The only place CodeAlta is taken from, whatever feeds the user has: CODEALTA_NUGET_SOURCE names a mirror of nuget.org.
    $nugetSource = if ($env:CODEALTA_NUGET_SOURCE) { $env:CODEALTA_NUGET_SOURCE } else { 'https://api.nuget.org/v3/index.json' }
    $dotnetInstallUrl = 'https://dot.net/v1/dotnet-install.ps1'
    $dotnetDownloadPage = 'https://dotnet.microsoft.com/download/dotnet/10.0'
    $dotnetChannel = '10.0'
    $webView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
    $webView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $guide = 'https://codealta.io/docs/getting-started/'

    # --- Output ---------------------------------------------------------------------------------------------------------

    $redirected = $false
    try { $redirected = [Console]::IsOutputRedirected } catch { $redirected = $true }
    $color = $Host.UI.SupportsVirtualTerminal -and -not $redirected -and -not $env:NO_COLOR
    $esc = [string][char]27
    $style = @{ Reset = ''; Bold = ''; Dim = ''; Ok = ''; Warn = ''; Err = ''; Accent = ''; Code = '' }
    if ($color) {
        $style = @{
            Reset = "$esc[0m"; Bold = "$esc[1m"; Dim = "$esc[2m"
            Ok = "$esc[38;2;74;222;128m"; Warn = "$esc[38;2;250;204;21m"; Err = "$esc[38;2;248;113;113m"
            Accent = "$esc[38;2;0;209;255m"; Code = "$esc[38;2;234;242;255m"
        }
    }
    # The font of the classic console has no check mark; Windows Terminal has.
    $check = if ($env:WT_SESSION) { [string][char]0x2713 } else { [string][char]0x221A }
    $dot = [string][char]0x00B7

    function Write-Ok($text) { Write-Host "  $($style.Ok)$check$($style.Reset) $text" }
    function Write-Info($text) { Write-Host "  $($style.Dim)$dot $text$($style.Reset)" }
    function Write-Warn($text) { Write-Host "  $($style.Warn)!$($style.Reset) $text" }
    function Write-Detail($text) { Write-Host "    $($style.Dim)$text$($style.Reset)" }
    function Dim($text) { "$($style.Dim)$dot $text$($style.Reset)" }

    # Stops the installation with a message: caught at the end of this block, which says it and returns.
    function Stop-Install([string] $message, [string[]] $help = @()) {
        $failure = New-Object System.Exception $message
        $failure.Data['CodeAltaInstall'] = $true
        $failure.Data['Help'] = $help
        throw $failure
    }

    # The logo of the website: "Code" in its soft white, "Alta" in the gradient of the brand (cyan, indigo, purple),
    # one color for each column. Drawn here with # for a full block and : for a light shade.
    function Write-Banner {
        $logo = @(
            '   ######                  ##                ##       ##    ##',
            '  ##::::##                :##               ####     :##   :##',
            ' ##    ::    ######       :##   #####      ##::##    :##  ######   ######',
            ':##         ##::::##   ######  ##:::##    ##  ::##   :## :::##:   ::::::##',
            ':##        :##   :##  ##:::## :#######   ##########  :##   :##     #######',
            '::##    ## :##   :## :##  :## :##::::   :##::::::##  :##   :##    ##::::##',
            ' ::######  ::######  ::###### ::######  :##     :##  ###   ::##  ::########',
            '  ::::::    ::::::    ::::::   ::::::   ::      ::  :::     ::    ::::::::'
        )
        $cut = 40
        $span = 34
        $full = [string][char]0x2588
        $shade = [string][char]0x2591
        $width = 80
        try { $width = $Host.UI.RawUI.WindowSize.Width } catch { }
        Write-Host ''
        if ($width -lt 78) {
            Write-Host "  $($style.Bold)$($style.Code)Code$($style.Accent)Alta$($style.Reset)"
        }
        elseif (-not $color) {
            foreach ($line in $logo) {
                $text = $line.Replace('#', $full).Replace(':', $shade)
                Write-Host ('  ' + $text.Substring(0, [Math]::Min($cut, $text.Length))) -NoNewline -ForegroundColor White
                if ($text.Length -gt $cut) { Write-Host $text.Substring($cut) -NoNewline -ForegroundColor Cyan }
                Write-Host ''
            }
        }
        else {
            foreach ($line in $logo) {
                $text = New-Object System.Text.StringBuilder '  '
                for ($i = 0; $i -lt $line.Length; $i++) {
                    $c = $line[$i]
                    if ($c -eq ' ') { [void]$text.Append(' '); continue }
                    $glyph = if ($c -eq '#') { $full } else { $shade }
                    if ($i -lt $cut) { $r = 234; $g = 242; $b = 255 }
                    else {
                        $t = [Math]::Min(1.0, ($i - $cut) / $span)
                        if ($t -lt 0.55) { $u = $t / 0.55; $r = 0 + 79 * $u; $g = 209 + (70 - 209) * $u; $b = 255 + (229 - 255) * $u }
                        else { $u = ($t - 0.55) / 0.45; $r = 79 + (168 - 79) * $u; $g = 70 + (85 - 70) * $u; $b = 229 + (247 - 229) * $u }
                    }
                    [void]$text.Append("$esc[38;2;$([int]$r);$([int]$g);$([int]$b)m$glyph")
                }
                Write-Host ($text.ToString() + $style.Reset)
            }
        }
        Write-Host ''
        Write-Host "  $($style.Dim)Your agents. One workspace. One fleet.$($style.Reset)"
        Write-Host ''
    }

    # --- Processes ------------------------------------------------------------------------------------------------------

    function Quote-Argument([string] $value) {
        if ($value -match '[\s"]') { '"' + $value.Replace('"', '\"') + '"' } else { $value }
    }

    # Runs a program with its output kept, behind a spinner on a terminal. Returns its exit code; $state.Log has what
    # it wrote.
    function Invoke-Quiet([string] $label, [string] $file, [string[]] $arguments) {
        $out = Join-Path $work 'output.log'
        $err = Join-Path $work 'error.log'
        $line = ($arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
        $process = Start-Process -FilePath $file -ArgumentList $line -WorkingDirectory $work -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
        # Without its handle read now, the exit code of a process that ended is lost.
        $null = $process.Handle
        if ($redirected) {
            Write-Info $label
            $process.WaitForExit()
        }
        else {
            $frames = '|', '/', '-', '\'
            $frame = 0
            while (-not $process.HasExited) {
                [Console]::Write("`r  $($style.Accent)$($frames[$frame % 4])$($style.Reset) $label")
                $frame++
                Start-Sleep -Milliseconds 100
            }
            $process.WaitForExit()
            [Console]::Write("`r" + (' ' * ($label.Length + 6)) + "`r")
        }
        $state.Log = @(Get-Content -LiteralPath $out, $err -ErrorAction SilentlyContinue)
        return $process.ExitCode
    }

    function Write-Log {
        $state.Log | Where-Object { $_ -and $_.Trim() } | Select-Object -Last 15 | ForEach-Object { Write-Detail $_ }
    }

    # The lines a program writes. Windows PowerShell turns what a program writes to its error stream into an error when
    # errors stop the script: they do not stop it here.
    function Read-Program([string] $file, [string[]] $arguments) {
        $ErrorActionPreference = 'Continue'
        try { @(& $file @arguments 2>$null) } catch { @() }
    }

    function Save-Download([string] $url, [string] $path) {
        Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing
    }

    # --- .NET -----------------------------------------------------------------------------------------------------------

    # The newest SDK of a dotnet that has what CodeAlta needs: an SDK 10 or later (dotnet tool is part of the SDK) and the
    # released .NET 10 runtimes its apps run on, .NET and ASP.NET Core. Null when it does not.
    function Get-FittingSdk([string] $dotnet) {
        $sdks = Read-Program $dotnet @('--list-sdks') | ForEach-Object { ("$_" -split ' ')[0] } |
            Where-Object { $_ -match '^(\d+)\.(\d+)\.(\d+)' -and [int]$Matches[1] -ge 10 } |
            Sort-Object { [version](($_ -split '-')[0]) }
        if (-not $sdks) { return $null }
        # A preview runtime (10.0.0-rc.1) does not run a released application.
        $runtimes = Read-Program $dotnet @('--list-runtimes')
        foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App') {
            if (-not ($runtimes | Where-Object { "$_" -match "^$([regex]::Escape($framework)) 10\.\d+\.\d+ " })) { return $null }
        }
        return @($sdks)[-1]
    }

    # The folder of the .NET installation a dotnet runs, as it says itself; the folder of the program otherwise.
    function Get-DotnetRoot([string] $dotnet) {
        foreach ($line in Read-Program $dotnet @('--list-runtimes')) {
            if ("$line" -match '^Microsoft\.NETCore\.App \S+ \[(.+)\\shared\\Microsoft\.NETCore\.App\]$') { return $Matches[1] }
        }
        return Split-Path $dotnet
    }

    # Looks for a dotnet that fits, on the PATH and where .NET is usually installed: $state.Dotnet and $state.Sdk,
    # otherwise $state.OtherDotnet for a dotnet that does not fit, when there is one.
    function Find-Dotnet {
        $state.Dotnet = $null
        $state.OtherDotnet = $null
        $candidates = @()
        $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $candidates += $command.Source }
        if ($env:DOTNET_ROOT) { $candidates += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
        $candidates += Join-Path $userDotnet 'dotnet.exe'
        if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
        foreach ($candidate in $candidates) {
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
            $sdk = Get-FittingSdk $candidate
            if ($sdk) {
                $state.Dotnet = (Get-Item -LiteralPath $candidate).FullName
                $state.Sdk = $sdk
                return $true
            }
            if (-not $state.OtherDotnet) { $state.OtherDotnet = (Get-Item -LiteralPath $candidate).FullName }
        }
        return $false
    }

    # Installs the .NET SDK in the folder of the user with Microsoft's dotnet-install script.
    function Install-UserDotnet {
        $script = Join-Path $work 'dotnet-install.ps1'
        try { Save-Download $dotnetInstallUrl $script }
        catch { Stop-Install "The install script of .NET could not be downloaded from $dotnetInstallUrl." @('Check your connection, then run this again.') }
        $code = Invoke-Quiet "Installing the runtime (.NET $dotnetChannel, a few minutes on a slow connection)" $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script,
            '-Channel', $dotnetChannel, '-InstallDir', $userDotnet, '-Architecture', $arch, '-NoPath')
        if ($code -eq 0) { return }
        Write-Log
        Stop-Install ".NET $dotnetChannel could not be installed in $userDotnet." @("Install the .NET $dotnetChannel SDK from $dotnetDownloadPage, then run this again.")
    }

    # Updates a .NET that belongs to the system (Program Files): with winget, else with the installer of Microsoft.
    function Install-SystemDotnet {
        Write-Info "A .NET without the .NET $dotnetChannel SDK is installed in $systemDotnet`: updating it. Windows asks for your permission."
        $winget = Get-Command winget -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($winget) {
            $null = Invoke-Quiet "Installing .NET $dotnetChannel with winget" $winget.Source @('install', '--id', 'Microsoft.DotNet.SDK.10', '--exact', '--silent',
                '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
            if (Find-Dotnet) { return }
        }
        $installer = Join-Path $work "dotnet-sdk-win-$arch.exe"
        try { Save-Download "https://aka.ms/dotnet/$dotnetChannel/dotnet-sdk-win-$arch.exe" $installer }
        catch { Stop-Install "The .NET $dotnetChannel SDK could not be downloaded." @("Install it from $dotnetDownloadPage, then run this again.") }
        try { $null = Invoke-Quiet "Installing .NET $dotnetChannel" $installer @('/install', '/quiet', '/norestart') }
        catch { Stop-Install "The installer of .NET $dotnetChannel did not run: $($_.Exception.Message)" @("Install the .NET $dotnetChannel SDK from $dotnetDownloadPage, then run this again.") }
    }

    function Initialize-Dotnet {
        # A .NET that fits is used as it is, wherever it is: nothing is installed.
        if (Find-Dotnet) { }
        # A .NET of the system that is too old is updated by the system, so that the system keeps one .NET. Anything else
        # (no .NET, one the user keeps in a folder of their own) is left as it is, and .NET goes to the folder of the user.
        elseif ($state.OtherDotnet -and (Get-DotnetRoot $state.OtherDotnet) -eq $systemDotnet) { Install-SystemDotnet }
        else { Install-UserDotnet }
        if (-not (Find-Dotnet)) {
            Write-Log
            Stop-Install "The .NET $dotnetChannel SDK was installed, but no dotnet that has it is found." @("Install it from $dotnetDownloadPage, then run this again.")
        }
        $state.DotnetDir = Get-DotnetRoot $state.Dotnet
        Write-Ok "Runtime $(Dim ".NET SDK $($state.Sdk) $dot $($state.DotnetDir)")"
        # For the commands below. DOTNET_ROOT is what the tools find their runtime with when .NET is not in Program Files.
        $state.ShellDotnetRoot = $env:DOTNET_ROOT
        if ($state.DotnetDir -ne $systemDotnet) { $env:DOTNET_ROOT = $state.DotnetDir }
        if (-not $env:DOTNET_CLI_TELEMETRY_OPTOUT) { $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $state.Restore += 'DOTNET_CLI_TELEMETRY_OPTOUT' }
        if (-not $env:DOTNET_NOLOGO) { $env:DOTNET_NOLOGO = '1'; $state.Restore += 'DOTNET_NOLOGO' }
    }

    # --- WebView2 -------------------------------------------------------------------------------------------------------

    function Get-WebView2Version {
        foreach ($key in "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$webView2Client",
            "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$webView2Client", "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$webView2Client") {
            $version = (Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue).pv
            if ($version -and $version -ne '0.0.0.0') { return $version }
        }
        return $null
    }

    # The desktop app draws its window with WebView2: part of Windows 11, and of a Windows 10 that is up to date.
    function Initialize-WebView2 {
        if ($apps -notcontains 'desktop') { return }
        $version = Get-WebView2Version
        if (-not $version) {
            try {
                $setup = Join-Path $work 'MicrosoftEdgeWebview2Setup.exe'
                Save-Download $webView2Url $setup
                $null = Invoke-Quiet 'Installing the WebView2 runtime, for the window of the desktop app' $setup @('/silent', '/install')
            }
            catch { }
            $version = Get-WebView2Version
        }
        if ($version) { Write-Ok "WebView2 $version" }
        else {
            # Not a reason to stop: CodeAlta says what it lacks when its window cannot open.
            $state.DesktopReady = $false
            Write-Warn 'The desktop app needs the WebView2 runtime, which could not be installed.'
            Write-Detail 'Install it from https://developer.microsoft.com/microsoft-edge/webview2/'
        }
    }

    # --- CodeAlta -------------------------------------------------------------------------------------------------------

    function Get-InstalledVersion([string] $package) {
        foreach ($line in Read-Program $state.Dotnet @('tool', 'list', '-g')) {
            $columns = "$line" -split '\s+'
            if ($columns.Count -ge 2 -and $columns[0] -eq $package) { return $columns[1] }
        }
        return $null
    }

    function Install-App([string] $package, [string] $title, [string] $command) {
        $before = Get-InstalledVersion $package
        # Windows does not replace the files of a program that runs.
        $running = @(Get-Process -Name $command -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($tools, [StringComparison]::OrdinalIgnoreCase) })
        if ($before -and $running.Count -gt 0) {
            Write-Warn "$title $before is running: it is left as it is."
            Write-Detail 'To update it, use Update and restart in the app, or exit it and run this again.'
            return
        }
        $arguments = @('tool', 'update', '-g', $package, '--source', $nugetSource)
        if ($Options.Version) { $arguments += @('--version', $Options.Version, '--allow-downgrade') }
        elseif ($Options.Prerelease) { $arguments += '--prerelease' }
        $code = Invoke-Quiet "Installing $title" $state.Dotnet $arguments
        if ($code -ne 0) {
            Write-Log
            Stop-Install "$title could not be installed." @("The command was:  dotnet $($arguments -join ' ')", "Help: $guide")
        }
        $after = Get-InstalledVersion $package
        if (-not $before) { Write-Ok "$title $after" }
        elseif ($before -eq $after) { Write-Ok "$title $after $(Dim 'up to date')" }
        else { Write-Ok "$title $after $(Dim "updated from $before")" }
    }

    # --- PATH -----------------------------------------------------------------------------------------------------------

    function Test-OnPath([string] $list, [string] $folder) {
        foreach ($entry in "$list" -split ';') {
            if ($entry -and [Environment]::ExpandEnvironmentVariables($entry).TrimEnd('\') -eq $folder.TrimEnd('\')) { return $true }
        }
        return $false
    }

    # Makes the commands found in new terminals and in this one: the folder of the .NET tools on the PATH of the user,
    # and .NET itself, with DOTNET_ROOT, when this script installed it in the folder of the user.
    function Initialize-Path {
        $folders = @()
        if ($state.DotnetDir -eq $userDotnet) { $folders += $userDotnet }
        $folders += $tools
        $changed = $false
        if ($Options.NoModifyPath) {
            foreach ($folder in $folders) { if (-not (Test-OnPath $env:Path $folder)) { $env:Path = $env:Path.TrimEnd(';') + ';' + $folder } }
            Write-Info 'The PATH of your account is left as it is.'
            $state.ByPath = $true
            return
        }
        # Read and written as they are in the registry: %VARIABLES% of the PATH stay variables.
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
        try {
            $userPath = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
            foreach ($folder in $folders) {
                if (-not (Test-OnPath $userPath $folder) -and -not (Test-OnPath $machinePath $folder)) {
                    $userPath = if ($userPath.Trim(';')) { $userPath.TrimEnd(';') + ';' + $folder } else { $folder }
                    $changed = $true
                }
                if (-not (Test-OnPath $env:Path $folder)) { $env:Path = $env:Path.TrimEnd(';') + ';' + $folder }
            }
            if ($changed) { $key.SetValue('Path', $userPath, [Microsoft.Win32.RegistryValueKind]::ExpandString) }
        }
        finally { $key.Close() }
        # The commands are small programs that look for .NET in Program Files: DOTNET_ROOT names one that is elsewhere.
        if ($state.DotnetDir -ne $systemDotnet -and $state.ShellDotnetRoot -ne $state.DotnetDir -and [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'User') -ne $state.DotnetDir) {
            # Tells the running programs too (the Start menu, new terminals) that the environment changed.
            [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $state.DotnetDir, 'User')
            $changed = $true
        }
        elseif ($changed) {
            # Setting a variable is what tells them: this one does not exist, and is removed.
            [Environment]::SetEnvironmentVariable('CODEALTA_INSTALL_PATH', $null, 'User')
        }
        if ($changed) { Write-Ok "PATH $(Dim 'of your account')" }
    }

    # --- Installation ---------------------------------------------------------------------------------------------------

    $state = @{ Log = @(); Restore = @(); Located = $false; Dotnet = $null; OtherDotnet = $null; Sdk = $null; DotnetDir = $null; ShellDotnetRoot = $null; ByPath = $false; DesktopReady = $true }
    $work = $null
    $failed = $false
    try {
        if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
            Stop-Install 'This script is for Windows.' @('On macOS and Linux, run:  curl -fsSL https://codealta.io/install.sh | sh')
        }
        $apps = @()
        if ($Options.All) { $apps = @('desktop', 'tui', 'cli') }
        else {
            if ($Options.Desktop) { $apps += 'desktop' }
            if ($Options.Tui) { $apps += 'tui' }
            if ($Options.Cli) { $apps += 'cli' }
            if ($apps.Count -eq 0) { $apps = @('desktop') }
        }

        Write-Banner

        # The processor of the machine, not of this PowerShell, which may be an x64 one on ARM64.
        $processor = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -ErrorAction SilentlyContinue).PROCESSOR_ARCHITECTURE
        if (-not $processor) { $processor = $env:PROCESSOR_ARCHITECTURE }
        $arch = switch ($processor) { 'AMD64' { 'x64' } 'ARM64' { 'arm64' } default { $null } }
        if (-not $arch) { Stop-Install "CodeAlta runs on x64 and arm64 processors: $processor is not supported." }
        $build = [Environment]::OSVersion.Version.Build
        $windows = if ($build -ge 22000) { 'Windows 11' } else { 'Windows 10' }
        if ($build -lt 17763) { Stop-Install "CodeAlta needs Windows 10 version 1809 or later: this is build $build." }
        Write-Ok "$windows $(Dim "build $build") $dot $arch"

        # Windows PowerShell asks for TLS 1.0 by default, which the download servers refuse.
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

        $work = Join-Path ([IO.Path]::GetTempPath()) ('codealta-install-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $work
        # dotnet reads the global.json and the nuget.config of the folder it runs in: not the ones of a project the
        # terminal happens to be in.
        Push-Location -LiteralPath $work
        $state.Located = $true
        # The PowerShell that runs this, to run the install script of .NET; another host (the ISE) cannot run a file.
        $shell = (Get-Process -Id $PID).Path
        if ((Split-Path $shell -Leaf) -notmatch '^(powershell|pwsh)\.exe$') { $shell = Join-Path $PSHOME 'powershell.exe' }
        $userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
        # Where the installers of .NET put it, and where programs look for it by themselves.
        $systemDotnet = Join-Path $env:ProgramFiles 'dotnet'
        $tools = Join-Path $env:USERPROFILE '.dotnet\tools'

        Initialize-Dotnet
        Initialize-WebView2
        $commands = @()
        foreach ($app in $apps) {
            switch ($app) {
                'desktop' { Install-App 'codealta' 'CodeAlta Desktop' 'alta'; $commands += 'alta' }
                'tui' { Install-App 'codealta.tui' 'CodeAlta TUI' 'altatui'; $commands += 'altatui' }
                'cli' { Install-App 'codealta.cli' 'CodeAlta CLI' 'altacli'; $commands += 'altacli' }
            }
        }
        Initialize-Path

        $first = $commands[0]
        # The command of a tool is a program or, for a tool with a package for each system, a batch file that starts it.
        if (-not (Test-Path -LiteralPath (Join-Path $tools "$first.exe")) -and -not (Test-Path -LiteralPath (Join-Path $tools "$first.cmd"))) { Stop-Install "$first was installed, but is not found in $tools." @("Help: $guide") }
        Write-Host ''
        # A script run from a file has a PowerShell of its own: the terminal that started it keeps its old PATH.
        $newTerminal = $FromFile -and -not $state.ByPath
        $launch = if ($FromFile -and $state.ByPath) { Join-Path $tools $first } else { $first }
        if ($first -eq 'alta' -and -not $state.DesktopReady) {
            Write-Host "  $($style.Bold)CodeAlta Desktop is installed, but its window cannot open yet.$($style.Reset)"
            $then = if ($newTerminal) { 'open a new terminal and start it from a project folder:' } else { 'start it from a project folder:' }
            Write-Host "  Do what the lines marked $($style.Warn)!$($style.Reset) above say, then $then"
        }
        elseif ($newTerminal) { Write-Host "  $($style.Bold)CodeAlta is ready.$($style.Reset) Open a new terminal, then start it from a project folder:" }
        else { Write-Host "  $($style.Bold)CodeAlta is ready.$($style.Reset) Start it from a project folder:" }
        Write-Host ''
        Write-Host "      $($style.Bold)$($style.Accent)$launch$($style.Reset)"
        Write-Host ''
        foreach ($other in $commands | Select-Object -Skip 1) { Write-Detail "$other is installed too." }
        Write-Detail "First steps: $guide"
        Write-Host ''
    }
    catch {
        $failed = $true
        $failure = $_.Exception
        Write-Host ''
        Write-Host "  $($style.Err)x $($failure.Message)$($style.Reset)"
        if ($failure.Data['CodeAltaInstall']) { foreach ($line in $failure.Data['Help']) { Write-Host "    $line" } }
        else { Write-Host "    $($_.ScriptStackTrace)"; Write-Host "    Help: $guide" }
        Write-Host ''
    }
    finally {
        if ($state.Located) { Pop-Location }
        foreach ($name in $state.Restore) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        if ($work -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
    # Run from a file, the exit code says how it went; run in a session, nothing ends it.
    if ($failed -and $FromFile) { exit 1 }
} @args
