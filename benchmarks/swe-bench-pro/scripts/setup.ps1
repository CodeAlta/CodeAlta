# Prepares a SWE-bench Pro v2 HARD-51 run: the pinned benchmark checkout, Harbor in a virtual environment, the Docker and
# kernel checks, and the dual glibc/musl altacli package of the CodeAlta adapter (benchmarks/harbor).
# By default the package is built from this checkout as it is (local changes included, recorded as dirty); with
# -CodeAltaRevision it is built from a detached worktree of that revision under workspace/, and this checkout is not
# touched.
[CmdletBinding()]
param(
    [string]$BundleRoot = (Split-Path $PSScriptRoot -Parent),

    # The CodeAlta checkout the package is built from; defaults to the repository that holds this kit.
    [string]$CodeAltaRoot,

    # A commit, tag or branch to build instead of the checkout as it is, from a detached worktree.
    [string]$CodeAltaRevision,

    [string]$BenchmarkCommit = "66f92766bba642462d4bbe5479e83f91f9211862",

    [string]$HarborVersion = "0.24.0",

    # The build configuration of altacli. Use the one that no running CodeAlta of this checkout uses (AGENTS.md).
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$bundle = [IO.Path]::GetFullPath($BundleRoot)
$workspace = Join-Path $bundle "workspace"
$repositoryRoot = if ([string]::IsNullOrWhiteSpace($CodeAltaRoot)) {
    [IO.Path]::GetFullPath((Join-Path $bundle "..\.."))
} else {
    [IO.Path]::GetFullPath($CodeAltaRoot)
}
$benchmark = Join-Path $workspace "SWE-bench_Pro-os"
$venv = Join-Path $workspace "harbor-venv"
$python = Join-Path $venv "Scripts\python.exe"
$harbor = Join-Path $venv "Scripts\harbor.exe"
$configPath = Join-Path $bundle "workspace.json"

function Assert-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command was not found: $Name"
    }
}

function Assert-ExitCode {
    param([string]$Operation)
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Git {
    param([string[]]$Arguments, [string]$Operation)
    & git @Arguments
    Assert-ExitCode $Operation
}

# Docker Compose on Windows fails with WinError 267 when the paths of the task sandboxes get too long.
if ($bundle.Length -gt 80) {
    throw "Kit path is $($bundle.Length) characters. Use a checkout at a shorter path, or copy benchmarks\swe-bench-pro to one such as C:\sbp-v2 and pass -CodeAltaRoot."
}

foreach ($command in @("git", "docker", "dotnet", "python", "tar")) {
    Assert-Command $command
}

$dockerInfo = (& docker info --format "{{.OSType}}|{{.KernelVersion}}|{{.NCPU}}|{{.MemTotal}}").Trim()
Assert-ExitCode "Reading Docker information"
$dockerParts = $dockerInfo -split "\|"
if ($dockerParts[0] -ne "linux") {
    throw "Docker must use Linux containers; current OSType is '$($dockerParts[0])'."
}
if ([long]$dockerParts[3] -lt 38GB) {
    throw "Docker exposes only $([math]::Round([long]$dockerParts[3] / 1GB, 1)) GiB. Eight workers require at least 38 GiB."
}

$sdkMajors = @(
    dotnet --list-sdks |
        ForEach-Object { if ($_ -match "^(\d+)\.") { [int]$Matches[1] } }
)
if ($sdkMajors -notcontains 10 -and -not ($sdkMajors | Where-Object { $_ -gt 10 })) {
    throw ".NET SDK 10 or later is required."
}

New-Item -ItemType Directory -Force -Path $workspace | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot ".git"))) {
    throw "The CodeAlta checkout is not a Git repository: $repositoryRoot"
}

# A requested revision is built from a worktree of its own: the checkout the user works in keeps its branch and changes.
$codeAlta = $repositoryRoot
if (-not [string]::IsNullOrWhiteSpace($CodeAltaRevision)) {
    # A local branch or commit (fixes not pushed yet) is used as it is; anything else is fetched from origin.
    $commit = (& git -C $repositoryRoot rev-parse --verify --quiet "$CodeAltaRevision^{commit}")
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        Invoke-Git @("-C", $repositoryRoot, "fetch", "origin", $CodeAltaRevision) "Fetching requested CodeAlta revision"
        $commit = (& git -C $repositoryRoot rev-parse --verify "FETCH_HEAD^{commit}")
        Assert-ExitCode "Resolving requested CodeAlta revision"
    }
    $commit = $commit.Trim()
    $codeAlta = Join-Path $workspace "codealta-$($commit.Substring(0, 12))"
    if (-not (Test-Path -LiteralPath (Join-Path $codeAlta ".git"))) {
        Invoke-Git @("-C", $repositoryRoot, "worktree", "add", "--detach", $codeAlta, $commit) "Creating the CodeAlta worktree"
    }
    if ((git -C $codeAlta status --porcelain)) {
        throw "The CodeAlta worktree has local changes: $codeAlta"
    }
}
$actualCodeAltaCommit = (& git -C $codeAlta rev-parse HEAD).Trim()
Assert-ExitCode "Reading CodeAlta revision"
# The kit's own outputs (workspace, jobs, datasets, results) are ignored, so they do not count as changes.
$codeAltaDirty = [bool](git -C $codeAlta status --porcelain)
if ($codeAltaDirty) {
    Write-Warning "CodeAlta has local changes. The package will be built from this exact dirty worktree."
}

if (-not (Test-Path -LiteralPath (Join-Path $benchmark ".git"))) {
    Invoke-Git @(
        "-c", "core.autocrlf=false",
        "clone", "--filter=blob:none", "--no-checkout",
        "https://github.com/scaleapi/SWE-bench_Pro-os.git", $benchmark
    ) "Cloning SWE-bench Pro"
    Invoke-Git @("-C", $benchmark, "sparse-checkout", "init", "--cone") "Initializing sparse checkout"
    Invoke-Git @("-C", $benchmark, "sparse-checkout", "set", "v2") "Selecting V2 benchmark files"
}
# With CRLF line endings the test patches no longer apply and every task fails; v2/SHA256SUMS checks it.
Invoke-Git @("-C", $benchmark, "config", "core.autocrlf", "false") "Disabling benchmark line-ending conversion"
Invoke-Git @("-C", $benchmark, "fetch", "origin", $BenchmarkCommit) "Fetching pinned benchmark revision"
Invoke-Git @("-C", $benchmark, "checkout", "--detach", $BenchmarkCommit) "Checking out pinned benchmark revision"
if ((git -C $benchmark status --porcelain)) {
    throw "SWE-bench checkout has local changes after checkout: $benchmark"
}

if (-not (Test-Path -LiteralPath $python)) {
    & python -m venv $venv
    Assert-ExitCode "Creating Harbor virtual environment"
}
& $python -m pip install --quiet "harbor==$HarborVersion"
Assert-ExitCode "Installing Harbor $HarborVersion"

$actualHarbor = (& $harbor --version).Trim()
if ($actualHarbor -ne $HarborVersion) {
    throw "Expected Harbor $HarborVersion, got $actualHarbor."
}

# Harbor's locked networking needs nftables CONFIG_NFT_FIB_INET (WSL kernel 6.18.54.1 passes, 5.15.167.4 does not).
$probe = @'
from harbor.environments.docker.docker import DockerEnvironment
supported = DockerEnvironment._egress_control_kernel_support()
print(f"Harbor locked-network kernel support: {supported}")
raise SystemExit(0 if supported else 1)
'@
$probe | & $python -
Assert-ExitCode "Checking Harbor locked-network kernel support"

$adapter = Join-Path $codeAlta "benchmarks\harbor"
if (-not $SkipBuild) {
    foreach ($path in @(
        (Join-Path $adapter "locked_codealta.py"),
        (Join-Path $adapter "build-altacli.ps1")
    )) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Selected CodeAlta version does not contain the required Harbor adapter file: $path"
        }
    }
    & (Join-Path $adapter "build-altacli.ps1") -Configuration $Configuration
    Assert-ExitCode "Building dual-runtime altacli package"
}

$tarball = Join-Path $adapter "dist\altacli-linux-x64.tar.gz"
$sidecar = "$tarball.sha256"
foreach ($file in @($tarball, $sidecar)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required benchmark package file was not found: $file"
    }
}
$expected = ((Get-Content -Raw -LiteralPath $sidecar) -split "\s+")[0]
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $tarball).Hash.ToLowerInvariant()
if ($expected -ne $actual) {
    throw "Dual-runtime altacli package checksum mismatch."
}

$config = [ordered]@{
    schema = 1
    created_at = (Get-Date).ToString("o")
    bundle_root = $bundle
    workspace_root = $workspace
    codealta_root = $codeAlta
    codealta_commit = $actualCodeAltaCommit
    codealta_dirty = $codeAltaDirty
    codealta_requested_revision = if ([string]::IsNullOrWhiteSpace($CodeAltaRevision)) { $null } else { $CodeAltaRevision }
    codealta_configuration = $Configuration
    altacli_sha256 = $actual
    benchmark_root = $benchmark
    benchmark_commit = $BenchmarkCommit
    harbor_python = $python
    harbor_executable = $harbor
    harbor_version = $HarborVersion
    altacli_tarball = $tarball
    docker = [ordered]@{
        os = $dockerParts[0]
        kernel = $dockerParts[1]
        cpus = [int]$dockerParts[2]
        memory_bytes = [long]$dockerParts[3]
    }
}
$config | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 -LiteralPath $configPath

Write-Host ""
Write-Host "Setup completed." -ForegroundColor Green
Write-Host "Workspace configuration: $configPath"
Write-Host "Next: pwsh -File .\scripts\run-hard51.ps1 -PrepareOnly"
