# Deletes the outputs of the kit (jobs, generated datasets, summaries), keeping the benchmark checkout, Harbor and the
# Docker images. -RemoveWorkspace also deletes the workspace (worktrees of requested revisions are pruned from Git);
# -RemoveSweBenchImages also deletes the SWE-bench Pro v2 images, which the next run downloads again.
[CmdletBinding()]
param(
    [string]$BundleRoot = (Split-Path $PSScriptRoot -Parent),

    [switch]$RemoveSweBenchImages,

    [switch]$RemoveWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$bundle = [IO.Path]::GetFullPath($BundleRoot)
$targets = @(
    (Join-Path $bundle "jobs"),
    (Join-Path $bundle "datasets"),
    (Join-Path $bundle "results")
)
if ($RemoveWorkspace) {
    $targets += Join-Path $bundle "workspace"
    $targets += Join-Path $bundle "workspace.json"
}

foreach ($target in $targets) {
    $resolved = [IO.Path]::GetFullPath($target)
    if (-not $resolved.StartsWith($bundle, [StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $bundle) {
        throw "Refusing unexpected cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

if ($RemoveWorkspace) {
    # The worktrees setup.ps1 made for a requested revision were deleted with the workspace.
    $repository = [IO.Path]::GetFullPath((Join-Path $bundle "..\.."))
    if (Test-Path -LiteralPath (Join-Path $repository ".git")) {
        git -C $repository worktree prune
    }
}

if ($RemoveSweBenchImages) {
    $imageIds = @(
        docker image ls --filter "reference=ghcr.io/scaleapi/swe-bench_pro-v2:*" --format "{{.ID}}" |
            Sort-Object -Unique
    )
    if ($imageIds.Count) {
        docker image rm $imageIds
        if ($LASTEXITCODE -ne 0) {
            throw "Removing SWE-bench V2 images failed with exit code $LASTEXITCODE."
        }
    }
}

Write-Host "Cleanup completed." -ForegroundColor Green
