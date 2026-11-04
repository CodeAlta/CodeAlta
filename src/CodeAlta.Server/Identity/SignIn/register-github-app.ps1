#Requires -Version 7.4
<#
.SYNOPSIS
Registers the CodeAlta GitHub App from its manifest: one confirmation in your browser.

.DESCRIPTION
Serves a page on a loopback port of this computer that posts the manifest (github-app-manifest.json, next to this
script) to the "new GitHub App" page of the organization on GitHub, and opens it in your browser. You confirm there with
"Create GitHub App"; GitHub sends your browser back to this script with a temporary code, which the script converts into
the app (POST /app-manifests/{code}/conversions, within the hour GitHub gives). It prints the id, the slug and the client
id of the app, and the steps left:

  1. Turn on "Enable Device Flow" in the settings of the app (a manifest cannot set it).
  2. Give CodeAlta Server the client id: PublicSignInClients.GitHubClientId in
     src/CodeAlta.Server/Identity/SignIn/SignInOptions.cs, or CODEALTA_SIGNIN_GITHUB_CLIENT_ID on a server until a
     release carries it.

The conversion also returns the client secret, the webhook secret and the private key of the app. CodeAlta signs people
in with the device flow, which needs none of them: the script neither prints nor saves them unless -SaveCredentialsTo
names a file.

.PARAMETER Organization
The organization that owns the app. CodeAlta by default.

.PARAMETER ManifestPath
The manifest of the app. github-app-manifest.json next to this script by default.

.PARAMETER GitHubUrl
The address of GitHub: https://github.com, or a GitHub Enterprise Server for a company's own app.

.PARAMETER ApiUrl
The address of GitHub's API: https://api.github.com for github.com, <GitHubUrl>/api/v3 for a GitHub Enterprise Server.

.PARAMETER Port
The loopback port the page is served on; 0 for a free one.

.PARAMETER SaveCredentialsTo
Saves the client secret, the webhook secret and the private key (PEM) of the app, as JSON, to this file, which must not
exist yet.
WARNING: whoever reads this file can act as the GitHub App, for every organization that installs it. Keep it out of any
repository and any synchronized folder, move it to a password manager or a secret store, then delete it. CodeAlta does
not need it; GitHub lets the owner make a new client secret or private key at any time, and revoke these.

.PARAMETER NoBrowser
Does not open the browser: open the address it prints yourself.

.PARAMETER TimeoutSeconds
How long the script waits for GitHub to send the browser back. One hour by default, the lifetime of GitHub's code.

.EXAMPLE
pwsh src/CodeAlta.Server/Identity/SignIn/register-github-app.ps1

.EXAMPLE
pwsh src/CodeAlta.Server/Identity/SignIn/register-github-app.ps1 -Organization Contoso -GitHubUrl https://github.contoso.example
#>
[CmdletBinding()]
param(
    [string] $Organization = "CodeAlta",
    [string] $ManifestPath = (Join-Path $PSScriptRoot "github-app-manifest.json"),
    [string] $GitHubUrl = "https://github.com",
    [string] $ApiUrl,
    [ValidateRange(0, 65535)] [int] $Port = 0,
    [string] $SaveCredentialsTo,
    [switch] $NoBrowser,
    [ValidateRange(1, 86400)] [int] $TimeoutSeconds = 3600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Organization -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$') { throw "'$Organization' is not the name of a GitHub organization." }
$pages = [Uri]::new($GitHubUrl.TrimEnd('/'))
if (-not $ApiUrl) {
    $ApiUrl = if ($pages.Host -eq "github.com") { "https://api.github.com" } else { "$($pages.AbsoluteUri.TrimEnd('/'))/api/v3" }
}
$ApiUrl = $ApiUrl.TrimEnd('/')

# The file of the credentials is checked before GitHub makes the app: a typo must not lose them.
$credentialsFile = $null
if ($SaveCredentialsTo) {
    $credentialsFile = [IO.Path]::GetFullPath($SaveCredentialsTo)
    if (Test-Path -LiteralPath $credentialsFile) { throw "$credentialsFile exists: name a new file." }
    $folder = Split-Path -Parent $credentialsFile
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "The folder $folder does not exist." }
}

$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json -AsHashtable

function New-State {
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Send-Response([IO.Stream] $stream, [int] $status, [string] $reason, [string] $html) {
    $body = [Text.Encoding]::UTF8.GetBytes($html)
    $head = "HTTP/1.1 $status $reason`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($body.Length)`r`nCache-Control: no-store`r`n" +
        "Referrer-Policy: no-referrer`r`nConnection: close`r`n`r`n"
    $bytes = [Text.Encoding]::ASCII.GetBytes($head)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Write($body, 0, $body.Length)
    $stream.Flush()
}

function Get-Query([string] $target) {
    $values = @{}
    $at = $target.IndexOf('?')
    if ($at -lt 0) { return $values }
    foreach ($pair in $target.Substring($at + 1).Split('&', [StringSplitOptions]::RemoveEmptyEntries)) {
        $parts = $pair.Split('=', 2)
        $values[[Uri]::UnescapeDataString($parts[0])] = if ($parts.Length -gt 1) { [Uri]::UnescapeDataString($parts[1].Replace('+', ' ')) } else { "" }
    }
    $values
}

function Get-Page([string] $title, [string] $content) {
    $encoded = [Net.WebUtility]::HtmlEncode($title)
    "<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"><title>$encoded</title>" +
    "<style>body{font-family:system-ui,sans-serif;max-width:34rem;margin:4rem auto;padding:0 1rem;line-height:1.45}</style></head>" +
    "<body><h1>$encoded</h1>$content</body></html>"
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$listener.Start()
$code = $null
try {
    $address = "http://127.0.0.1:$(([Net.IPEndPoint]$listener.LocalEndpoint).Port)"
    $manifest["redirect_url"] = "$address/redirect"
    $state = New-State
    $action = "$($pages.AbsoluteUri.TrimEnd('/'))/organizations/$Organization/settings/apps/new?state=$state"
    $json = $manifest | ConvertTo-Json -Depth 10 -Compress
    $form = "<p>Sending the manifest of the app to GitHub. Confirm there with <strong>Create GitHub App</strong>.</p>" +
        "<form id=`"manifest`" method=`"post`" action=`"$([Net.WebUtility]::HtmlEncode($action))`">" +
        "<input type=`"hidden`" name=`"manifest`" value=`"$([Net.WebUtility]::HtmlEncode($json))`">" +
        "<button type=`"submit`">Continue to GitHub</button></form>" +
        "<script>document.getElementById('manifest').submit();</script>"

    Write-Host "Registering the GitHub App '$($manifest["name"])' of the organization $Organization at $($pages.AbsoluteUri.TrimEnd('/'))."
    Write-Host "Listening on $address/"
    if ($NoBrowser) {
        Write-Host "Open $address/ in your browser."
    }
    else {
        try { Start-Process "$address/" | Out-Null }
        catch { Write-Host "The browser did not open: open $address/ yourself." }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $code) {
        $accept = $listener.AcceptTcpClientAsync()
        # Short waits, so that Ctrl+C stops the script.
        while (-not $accept.Wait(500)) {
            if ([DateTime]::UtcNow -gt $deadline) { throw "GitHub did not send the browser back within $TimeoutSeconds seconds: run the script again." }
        }

        $client = $accept.Result
        try {
            $client.ReceiveTimeout = 10000
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 4096, $true)
            $line = $reader.ReadLine()
            while (($header = $reader.ReadLine())) { }
            $parts = if ($line) { $line.Split(' ') } else { @() }
            if ($parts.Length -lt 2 -or $parts[0] -ne "GET") {
                Send-Response $stream 405 "Method Not Allowed" (Get-Page "Not here" "<p>This page only answers GET.</p>")
                continue
            }

            $target = $parts[1]
            $path = ($target -split '\?', 2)[0]
            if ($path -eq "/") {
                Send-Response $stream 200 "OK" (Get-Page "Register the CodeAlta GitHub App" $form)
            }
            elseif ($path -eq "/redirect") {
                $query = Get-Query $target
                if (-not $query.ContainsKey("state") -or $query["state"] -ne $state) {
                    # Another page cannot hand this script a code of its own.
                    Write-Host "Refused a redirect that does not carry the state of this registration."
                    Send-Response $stream 400 "Bad Request" (Get-Page "Not this registration" "<p>This address does not come from the registration that runs. Go back to the terminal.</p>")
                }
                elseif (-not $query.ContainsKey("code") -or $query["code"] -notmatch '^[A-Za-z0-9_-]{1,256}$') {
                    Send-Response $stream 400 "Bad Request" (Get-Page "No code" "<p>GitHub sent no code. Go back to the terminal and run the script again.</p>")
                }
                else {
                    $code = $query["code"]
                    Send-Response $stream 200 "OK" (Get-Page "The GitHub App is made" "<p>Go back to the terminal: the script finishes the registration there. You can close this page.</p>")
                }
            }
            else {
                Send-Response $stream 404 "Not Found" (Get-Page "Not here" "<p>Nothing at this address.</p>")
            }
        }
        catch [IO.IOException] {
            # A browser that closed its connection early: wait for the next one.
        }
        finally {
            $client.Dispose()
        }
    }
}
finally {
    $listener.Stop()
}

$headers = @{ Accept = "application/vnd.github+json"; "X-GitHub-Api-Version" = "2022-11-28"; "User-Agent" = "CodeAlta-register-github-app" }
try {
    $app = Invoke-RestMethod -Method Post -Uri "$ApiUrl/app-manifests/$code/conversions" -Headers $headers -ContentType "application/json" -Body "{}"
}
catch {
    throw "GitHub did not convert the code into the app ($($_.Exception.Message)). The code works once and for one hour: run the script again."
}

$owner = if ($app.PSObject.Properties["owner"] -and $app.owner) { $app.owner.login } else { $Organization }
Write-Host ""
Write-Host "The GitHub App is registered."
Write-Host "  App id:    $($app.id)"
Write-Host "  Slug:      $($app.slug)"
Write-Host "  Client id: $($app.client_id)"
Write-Host "  Owner:     $owner"
Write-Host "  Page:      $($app.html_url)"

if ($credentialsFile) {
    $secrets = [ordered]@{
        app_id = $app.id; slug = $app.slug; client_id = $app.client_id; client_secret = $app.client_secret; webhook_secret = $app.webhook_secret; pem = $app.pem
    } | ConvertTo-Json
    # Readable by its owner only, before anything is written in it.
    $null = New-Item -ItemType File -Path $credentialsFile
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($credentialsFile, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
    [IO.File]::WriteAllText($credentialsFile, $secrets)
    Write-Host ""
    Write-Host "The client secret, the webhook secret and the private key are in $credentialsFile."
    Write-Host "Move them to a password manager or a secret store, then delete that file: CodeAlta does not need them."
}

$settings = "$($pages.AbsoluteUri.TrimEnd('/'))/organizations/$owner/settings/apps/$($app.slug)"
Write-Host ""
Write-Host "Left to do:"
Write-Host "  1. Open $settings, check 'Enable Device Flow' and save: a manifest cannot turn it on, and GitHub sign-in needs it."
Write-Host "  2. Give CodeAlta Server the client id $($app.client_id): PublicSignInClients.GitHubClientId in"
Write-Host "     src/CodeAlta.Server/Identity/SignIn/SignInOptions.cs, or CODEALTA_SIGNIN_GITHUB_CLIENT_ID=$($app.client_id) on a server"
Write-Host "     until a release carries it."
