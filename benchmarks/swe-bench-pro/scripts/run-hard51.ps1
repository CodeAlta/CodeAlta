# Runs SWE-bench Pro v2 HARD-51 under its locked protocol with Harbor, one harness job at a time, each followed by a
# pristine patch replay (the authoritative score). -PrepareOnly runs the model-free install preflights only; a paid run
# needs -ConfirmPaidRun. -Runs selects the jobs (the four of the comparison by default), for example only the CodeAlta
# ones after a change; codealta-cc-opus, CodeAlta driving the Claude Code CLI, runs only when named.
[CmdletBinding()]
param(
    [ValidateRange(1, 20)]
    [int]$Concurrency = 8,

    [string]$RunId = (Get-Date -Format "yyyyMMdd-HHmmss"),

    [string]$BundleRoot = (Split-Path $PSScriptRoot -Parent),

    # Names among codealta-opus, claude-code-opus, codealta-astra, codex-astra and codealta-cc-opus (not in the default);
    # under pwsh -File, one comma-separated argument (-Runs codealta-opus,codealta-astra).
    [string[]]$Runs = @("codealta-opus", "claude-code-opus", "codealta-astra", "codex-astra"),

    # More altacli run arguments for the CodeAlta jobs, as one string (the adapter's extra_args), for example
    # "--code-mode=off"; recorded in the run manifest.
    [string]$CodeAltaArgs,

    [ValidateRange(0, 3)]
    [int]$InfrastructureRetries = 1,

    [switch]$AllowMemoryOvercommit,

    [switch]$PrepareOnly,

    [switch]$ConfirmPaidRun,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$bundle = [IO.Path]::GetFullPath($BundleRoot)
$workspaceConfigPath = Join-Path $bundle "workspace.json"
$jobsRoot = Join-Path $bundle "jobs"
$datasetRoot = Join-Path $bundle "datasets\hard51"

# The jobs, in the order they run. CodeAlta / Opus runs CodeAlta's own agent loop on the Anthropic Messages API: the
# adapter maps -m anthropic/<model> to the `anthropic` provider type. CodeAlta CC / Opus (optional) runs CodeAlta's
# claude-code provider instead: CodeAlta drives the Claude Code CLI, which the adapter installs during setup.
$runDefinitions = [ordered]@{
    "codealta-opus" = [ordered]@{ Name = "codealta-opus55-xhigh"; Harness = "CodeAlta"; Agent = "locked_codealta:LockedCodeAlta"; Model = "anthropic/claude-opus-5-5"; Host = "api.anthropic.com"; Kwargs = @("reasoning_effort=xhigh", "disable_tools=web") }
    "claude-code-opus" = [ordered]@{ Name = "claude-code-opus55-xhigh"; Harness = "Claude Code"; Agent = "locked_claude_code:LockedClaudeCode"; Model = "anthropic/claude-opus-5-5"; Host = "api.anthropic.com"; Kwargs = @("reasoning_effort=xhigh", "disallowed_tools=WebFetch,WebSearch") }
    "codealta-astra" = [ordered]@{ Name = "codealta-astra-xhigh"; Harness = "CodeAlta"; Agent = "locked_codealta:LockedCodeAlta"; Model = "openai/gpt-6-astra"; Host = "api.openai.com"; Kwargs = @("reasoning_effort=xhigh", "disable_tools=web") }
    "codex-astra" = [ordered]@{ Name = "codex-astra-xhigh"; Harness = "Codex"; Agent = "locked_codex:LockedCodex"; Model = "openai/gpt-6-astra"; Host = "api.openai.com"; Kwargs = @("reasoning_effort=xhigh", "web_search=disabled") }
    "codealta-cc-opus" = [ordered]@{ Name = "codealta-cc-opus55-xhigh"; Harness = "CodeAlta on Claude Code"; Agent = "locked_codealta:LockedCodeAlta"; Model = "anthropic/claude-opus-5-5"; Host = "api.anthropic.com"; Kwargs = @("reasoning_effort=xhigh", "disable_tools=web", "provider_type=claude-code") }
}
$requestedRuns = @($Runs | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknownRuns = @($requestedRuns | Where-Object { -not $runDefinitions.Contains($_) })
if ($unknownRuns.Count -or -not $requestedRuns.Count) {
    throw "Unknown -Runs value '$($unknownRuns -join ", ")': use $($runDefinitions.Keys -join ", ")."
}
$selectedRuns = @($runDefinitions.Keys | Where-Object { $requestedRuns -contains $_ })

function Assert-File {
    param([string]$Path, [string]$Description)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
}

function Assert-ExitCode {
    param([string]$Operation)
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Format-CommandArgument {
    param([string]$Value)
    if ($Value -match '[\s"]') {
        return '"' + $Value.Replace('"', '\"') + '"'
    }
    return $Value
}

# Harbor can exit with 0 while its trials failed: the job's result.json says whether it completed cleanly.
function Assert-HarborJobSucceeded {
    param([string]$JobName)
    $resultPath = Join-Path (Join-Path $jobsRoot $JobName) "result.json"
    Assert-File $resultPath "Harbor result for $JobName"
    $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
    if ($null -eq $result.finished_at -or
        [int]$result.stats.n_errored_trials -ne 0 -or
        [int]$result.stats.n_running_trials -ne 0 -or
        [int]$result.stats.n_pending_trials -ne 0) {
        throw "Harbor job $JobName did not complete cleanly: completed=$($result.stats.n_completed_trials), errors=$($result.stats.n_errored_trials), running=$($result.stats.n_running_trials), pending=$($result.stats.n_pending_trials)."
    }
}

function Show-JobSummary {
    param([string]$JobName)
    $result = Get-Content -Raw -LiteralPath (Join-Path (Join-Path $jobsRoot $JobName) "result.json") | ConvertFrom-Json
    $evaluation = $result.stats.evals.PSObject.Properties | Select-Object -First 1 -ExpandProperty Value
    $metrics = @($evaluation.metrics)
    $mean = if ($metrics.Count) { [double]$metrics[0].mean } else { [double]::NaN }
    $duration = (Get-Date $result.finished_at) - (Get-Date $result.started_at)
    Write-Host ""
    Write-Host "Completed $JobName" -ForegroundColor Green
    Write-Host ("  Trials: {0}; errors: {1}; mean reward: {2:N3}; wall time: {3}" -f
        $evaluation.n_trials, $evaluation.n_errors, $mean, $duration.ToString("hh\:mm\:ss"))
    Write-Host ("  Input: {0}; cached: {1}; output: {2}; reported cost: {3}" -f
        $result.stats.n_input_tokens, $result.stats.n_cache_tokens, $result.stats.n_output_tokens, $result.stats.cost_usd)
}

Assert-File $workspaceConfigPath "Workspace configuration (run scripts\setup.ps1 first)"
$config = Get-Content -Raw -LiteralPath $workspaceConfigPath | ConvertFrom-Json
$codeAlta = [string]$config.codealta_root
$benchmark = [string]$config.benchmark_root
$harbor = [string]$config.harbor_executable
$python = [string]$config.harbor_python
$tooling = Join-Path $benchmark "v2\tooling"
$tasksRoot = Join-Path $benchmark "v2\tasks"
$hardIds = Join-Path $benchmark "v2\hard51_ids.txt"
$makeSubset = Join-Path $tooling "make_subset.py"
$adapter = Join-Path $codeAlta "benchmarks\harbor"
$tarball = Join-Path $adapter "dist\altacli-linux-x64.tar.gz"

foreach ($pair in @(
    @($harbor, "Harbor"),
    @($python, "Harbor Python"),
    @($hardIds, "HARD-51 id list"),
    @($makeSubset, "Official subset builder"),
    @($tarball, "Dual-runtime altacli package"),
    @("$tarball.sha256", "Dual-runtime package checksum"),
    @((Join-Path $adapter "locked_codealta.py"), "CodeAlta Harbor adapter"),
    @((Join-Path $tooling "locked_claude_code.py"), "Locked Claude Code adapter"),
    @((Join-Path $tooling "locked_codex.py"), "Locked Codex adapter"),
    @((Join-Path $tooling "patch_replay.py"), "Patch replay adapter")
)) {
    Assert-File $pair[0] $pair[1]
}

if ($bundle.Length -gt 80) {
    throw "Kit path is too long ($($bundle.Length) characters). Use a path such as C:\sbp-v2."
}

if (-not $DryRun) {
    $dockerMemoryText = & docker info --format "{{.MemTotal}}"
    Assert-ExitCode "Reading Docker memory capacity"
    $dockerMemoryBytes = [long]$dockerMemoryText
    $requiredDockerMemoryBytes = ($Concurrency * 4GB) + 6GB
    if (-not $AllowMemoryOvercommit -and $dockerMemoryBytes -lt $requiredDockerMemoryBytes) {
        throw "Docker exposes $([math]::Round($dockerMemoryBytes / 1GB, 1)) GiB; concurrency $Concurrency needs at least $([math]::Round($requiredDockerMemoryBytes / 1GB, 1)) GiB."
    }
}

$taskIds = @(
    Get-Content -LiteralPath $hardIds |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if ($taskIds.Count -ne 51) {
    throw "Expected 51 official HARD task ids, found $($taskIds.Count)."
}

if (-not $DryRun) {
    foreach ($taskId in $taskIds) {
        Assert-File (Join-Path $tasksRoot "$taskId\task.toml") "Task $taskId"
    }

    # The official checksums catch a checkout whose line endings were converted (the test patches would not apply).
    $manifestPath = Join-Path $benchmark "v2\SHA256SUMS"
    Assert-File $manifestPath "Official checksum manifest"
    $hashes = @{}
    foreach ($line in Get-Content -LiteralPath $manifestPath) {
        if ($line -match '^([0-9a-f]{64})\s+\*?(.+)$') {
            $hashes[$Matches[2]] = $Matches[1]
        }
    }
    foreach ($taskId in $taskIds) {
        $prefix = "tasks/$taskId/"
        $entries = @($hashes.GetEnumerator() | Where-Object {
            $_.Key.StartsWith($prefix, [StringComparison]::Ordinal)
        })
        if (-not $entries.Count) {
            throw "No checksum entries found for $taskId."
        }
        foreach ($entry in $entries) {
            $path = Join-Path (Join-Path $benchmark "v2") $entry.Key.Replace("/", "\")
            Assert-File $path "Manifest file $($entry.Key)"
            $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
            if ($actual -ne $entry.Value) {
                throw "Checksum mismatch: $($entry.Key)"
            }
        }
    }

    $expectedTarball = ((Get-Content -Raw -LiteralPath "$tarball.sha256") -split "\s+")[0]
    $actualTarball = (Get-FileHash -Algorithm SHA256 -LiteralPath $tarball).Hash.ToLowerInvariant()
    if ($expectedTarball -ne $actualTarball) {
        throw "Dual-runtime altacli package checksum mismatch."
    }
    if ($config.PSObject.Properties["altacli_sha256"] -and $config.altacli_sha256 -and $config.altacli_sha256 -ne $actualTarball) {
        throw "The altacli package changed since setup ($($config.altacli_sha256) -> $actualTarball): run scripts\setup.ps1 again so the manifest records what runs."
    }

    if (Test-Path -LiteralPath $datasetRoot) {
        Remove-Item -Recurse -Force -LiteralPath $datasetRoot
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $datasetRoot -Parent) | Out-Null
    & $python $makeSubset $tasksRoot $hardIds $datasetRoot
    Assert-ExitCode "Creating official HARD-51 dataset"
    $datasetTasks = @(Get-ChildItem -LiteralPath $datasetRoot -Directory | Select-Object -ExpandProperty Name | Sort-Object)
    $differences = @(Compare-Object @($taskIds | Sort-Object) $datasetTasks)
    if ($datasetTasks.Count -ne 51 -or $differences.Count) {
        throw "Generated dataset does not exactly match the official HARD-51 list."
    }
    New-Item -ItemType Directory -Force -Path $jobsRoot | Out-Null
}

$env:PYTHONPATH = "$tooling;$adapter"
$env:PYTHONUTF8 = "1"
$env:PYTHONIOENCODING = "utf-8"
$env:NO_COLOR = "1"
$env:TERM = "dumb"

function Invoke-HarborJob {
    param(
        [string]$JobName,
        [string]$Agent,
        [string]$Model,
        [string]$AllowedHost,
        [string[]]$AgentKwargs = @(),
        [string]$SourceJob,
        [switch]$InstallOnly,
        [int]$MaxRetries
    )

    $jobPath = Join-Path $jobsRoot $JobName
    if (-not $DryRun -and (Test-Path -LiteralPath $jobPath)) {
        throw "Job already exists: $jobPath. Use a new -RunId."
    }

    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.AddRange([string[]]@("run", "-p", $datasetRoot, "-e", "docker", "-n", $Concurrency.ToString(), "-a", $Agent, "-m", $Model))
    foreach ($kwarg in $AgentKwargs) {
        $arguments.Add("--ak")
        $arguments.Add($kwarg)
    }
    if ($SourceJob) {
        $arguments.Add("--ak")
        $arguments.Add("source_job=$SourceJob")
    }
    if ($AllowedHost) {
        $arguments.Add("--allow-agent-host")
        $arguments.Add($AllowedHost)
    }
    if ($InstallOnly) {
        $arguments.Add("--install-only")
    }
    $retries = if ($PSBoundParameters.ContainsKey("MaxRetries")) { $MaxRetries } else { $InfrastructureRetries }
    $arguments.Add("--max-retries")
    $arguments.Add($retries.ToString())
    if ($retries) {
        $arguments.Add("--retry-include")
        $arguments.Add("NetworkConnectionError")
    }
    $arguments.AddRange([string[]]@("--job-name", $JobName, "-o", $jobsRoot))

    Write-Host ""
    Write-Host "Starting $JobName" -ForegroundColor Cyan
    Write-Host ("& `"{0}`" {1}" -f $harbor, (($arguments | ForEach-Object { Format-CommandArgument $_ }) -join " "))
    if ($DryRun) {
        return
    }

    & $harbor @arguments
    Assert-ExitCode "Harbor job $JobName"
    Assert-HarborJobSucceeded $JobName
    Show-JobSummary $JobName
}

# Model-free install preflights, one per harness that runs: they check that each agent installs in all 51 images.
$preflights = [ordered]@{
    "CodeAlta" = [ordered]@{ Name = "codealta-install-preflight"; Agent = "locked_codealta:LockedCodeAlta"; Model = "anthropic/claude-opus-5-5"; Kwargs = @("reasoning_effort=xhigh") }
    "Claude Code" = [ordered]@{ Name = "claude-code-install-preflight"; Agent = "locked_claude_code:LockedClaudeCode"; Model = "anthropic/claude-opus-5-5"; Kwargs = @("reasoning_effort=xhigh") }
    "Codex" = [ordered]@{ Name = "codex-install-preflight"; Agent = "locked_codex:LockedCodex"; Model = "openai/gpt-6-astra"; Kwargs = @("reasoning_effort=xhigh", "web_search=disabled") }
    # altacli and the Claude Code CLI the claude-code provider runs.
    "CodeAlta on Claude Code" = [ordered]@{ Name = "codealta-cc-install-preflight"; Agent = "locked_codealta:LockedCodeAlta"; Model = "anthropic/claude-opus-5-5"; Kwargs = @("reasoning_effort=xhigh", "provider_type=claude-code") }
}
$harnesses = @($selectedRuns | ForEach-Object { $runDefinitions[$_].Harness } | Select-Object -Unique)
foreach ($harness in $preflights.Keys) {
    if ($harnesses -notcontains $harness) {
        continue
    }
    $preflight = $preflights[$harness]
    Invoke-HarborJob -JobName "$RunId-$($preflight.Name)" -Agent $preflight.Agent -Model $preflight.Model -AgentKwargs $preflight.Kwargs -InstallOnly -MaxRetries 0
}

if ($PrepareOnly) {
    Write-Host ""
    Write-Host "Preparation and all install-only preflights completed without model inference." -ForegroundColor Green
    return
}

if (-not $DryRun) {
    $models = @($selectedRuns | ForEach-Object { $runDefinitions[$_].Model })
    if (($models | Where-Object { $_ -like "anthropic/*" }) -and [string]::IsNullOrWhiteSpace($env:ANTHROPIC_API_KEY)) {
        throw "ANTHROPIC_API_KEY is not set."
    }
    if (($models | Where-Object { $_ -like "openai/*" }) -and [string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
        throw "OPENAI_API_KEY is not set."
    }
    if (-not $ConfirmPaidRun) {
        throw "Paid execution is blocked unless -ConfirmPaidRun is provided."
    }
}

$manifest = [ordered]@{
    schema = 1
    run_id = $RunId
    created_at = (Get-Date).ToString("o")
    task_count = 51
    concurrency = $Concurrency
    benchmark_commit = $config.benchmark_commit
    codealta_commit = $config.codealta_commit
    codealta_dirty = $config.codealta_dirty
    codealta_requested_revision = $config.codealta_requested_revision
    codealta_args = if ([string]::IsNullOrWhiteSpace($CodeAltaArgs)) { $null } else { $CodeAltaArgs }
    altacli_sha256 = if ($config.PSObject.Properties["altacli_sha256"]) { $config.altacli_sha256 } else { $null }
    harbor_version = $config.harbor_version
    jobs_root = $jobsRoot
    dataset = $datasetRoot
    runs = @($selectedRuns | ForEach-Object {
        $run = $runDefinitions[$_]
        [ordered]@{ name = "$RunId-$($run.Name)"; harness = $run.Harness; model = $run.Model; reasoning = "xhigh" }
    })
}
if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run: no Harbor job or manifest will be written." -ForegroundColor Yellow
} else {
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $jobsRoot "$RunId-manifest.json")
}

foreach ($key in $selectedRuns) {
    $run = $runDefinitions[$key]
    $jobName = "$RunId-$($run.Name)"
    $kwargs = @($run.Kwargs)
    if ($run.Agent -eq "locked_codealta:LockedCodeAlta" -and -not [string]::IsNullOrWhiteSpace($CodeAltaArgs)) {
        $kwargs += "extra_args=$CodeAltaArgs"
    }
    Invoke-HarborJob -JobName $jobName -Agent $run.Agent -Model $run.Model -AllowedHost $run.Host -AgentKwargs $kwargs
    Invoke-HarborJob -JobName "$jobName-regrade" -Agent "patch_replay:PatchReplayAgent" -Model "replay" -SourceJob (Join-Path $jobsRoot $jobName)
}

Write-Host ""
if ($DryRun) {
    Write-Host "Dry run completed." -ForegroundColor Green
} else {
    Write-Host "All model jobs and pristine regrades completed." -ForegroundColor Green
    Write-Host "Results: $jobsRoot"
    Write-Host "Summary: .\workspace\harbor-venv\Scripts\python.exe .\scripts\summarize-results.py --run-id $RunId"
}
