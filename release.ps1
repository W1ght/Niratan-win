[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Repo = 'W1ght/Niratan-win',

    [string]$Branch = 'main',

    [string]$Workflow = 'CI - Build and Package',

    [string]$NotesFile,

    [switch]$Draft,

    [switch]$Prerelease,

    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-ReleaseVersion {
    param([string]$RawVersion)

    $value = $RawVersion.Trim()
    if ($value.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(1)
    }

    if ($value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be stable semver in x.y.z or vx.y.z form. Received: $RawVersion"
    }

    [pscustomobject]@{
        Version = $value
        Tag = "v$value"
    }
}

function New-ReleasePlan {
    param(
        [string]$ReleaseVersion,
        [string]$Tag,
        [string]$RepoName,
        [string]$BranchName,
        [string]$WorkflowName
    )

    [pscustomobject]@{
        Version = $ReleaseVersion
        Tag = $Tag
        Repo = $RepoName
        Branch = $BranchName
        Workflow = $WorkflowName
        RunsLocalBuildOrTests = $false
        Steps = @(
            'Require clean main working tree'
            'Update Niratan.csproj release version and commit it if needed'
            'Push main and immutable release tag'
            'Wait for GitHub Actions packaging workflow to test, package, validate hoshidicts_c_api.dll, and create the release'
            'Validate GitHub Release assets created directly by GitHub Actions'
        )
    }
}

function Invoke-External {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Invoke-ExternalOutput {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$($output | Out-String)"
    }

    $output
}

function Assert-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
    }
}

function Assert-CleanMain {
    param([string]$BranchName)

    $currentBranch = (Invoke-ExternalOutput git @('branch', '--show-current') | Out-String).Trim()
    if ($currentBranch -ne $BranchName) {
        throw "Release must run from $BranchName. Current branch: $currentBranch"
    }

    $status = (Invoke-ExternalOutput git @('status', '--porcelain') | Out-String).Trim()
    if ($status.Length -gt 0) {
        throw "Release requires a clean working tree. Commit or stash changes before releasing.`n$status"
    }
}

function Assert-TagAvailable {
    param(
        [string]$Tag,
        [string]$RepoName
    )

    & git rev-parse -q --verify "refs/tags/$Tag" 1>$null 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw "Local tag already exists: $Tag. Release tags are immutable."
    }

    $remoteTag = (Invoke-ExternalOutput git @('ls-remote', '--tags', 'origin', "refs/tags/$Tag") | Out-String).Trim()
    if ($remoteTag.Length -gt 0) {
        throw "Remote tag already exists: $Tag. Create a new patch version instead."
    }

    & cmd /c "gh release view $Tag --repo $RepoName >NUL 2>NUL"
    if ($LASTEXITCODE -eq 0) {
        throw "GitHub Release already exists: $Tag. Create a new patch version instead."
    }
}

function Set-ProjectVersion {
    param(
        [string]$RepoRoot,
        [string]$ReleaseVersion
    )

    $projectPath = Join-Path $RepoRoot 'Niratan\Niratan.csproj'
    $content = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8

    if ($content -notmatch '<VersionPrefix>[^<]+</VersionPrefix>') {
        throw 'Niratan.csproj is missing VersionPrefix.'
    }

    if ($content -notmatch '<VersionSuffix>[^<]*</VersionSuffix>') {
        throw 'Niratan.csproj is missing VersionSuffix.'
    }

    $updated = $content -replace '<VersionPrefix>[^<]+</VersionPrefix>', "<VersionPrefix>$ReleaseVersion</VersionPrefix>"
    $updated = $updated -replace '<VersionSuffix>[^<]*</VersionSuffix>', '<VersionSuffix></VersionSuffix>'

    if ($updated -eq $content) {
        return $false
    }

    Set-Content -LiteralPath $projectPath -Value $updated -Encoding UTF8 -NoNewline
    return $true
}

function Wait-GitHubActionsRun {
    param(
        [string]$RepoName,
        [string]$WorkflowName,
        [string]$Tag,
        [string]$HeadSha
    )

    $deadline = (Get-Date).AddMinutes(10)
    do {
        $json = (Invoke-ExternalOutput gh @(
            'run', 'list',
            '--repo', $RepoName,
            '--workflow', $WorkflowName,
            '--event', 'push',
            '--limit', '20',
            '--json', 'databaseId,headBranch,headSha,status,conclusion,createdAt'
        ) | Out-String)

        $runs = @($json | ConvertFrom-Json)
        if ($runs.Count -eq 1 -and $runs[0] -is [System.Array]) {
            $runs = @($runs[0])
        }

        $run = $runs |
            Where-Object { $_.headBranch -eq $Tag -and $_.headSha -eq $HeadSha } |
            Sort-Object createdAt -Descending |
            Select-Object -First 1

        if ($run) {
            return [string]($run.databaseId)
        }

        Write-Host "Waiting for workflow run for $Tag..."
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for workflow '$WorkflowName' on $Tag."
}

function Assert-ChildPath {
    param(
        [string]$Parent,
        [string]$Child
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $childFull = [System.IO.Path]::GetFullPath($Child).TrimEnd('\', '/')
    $requiredPrefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar

    if (-not $childFull.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside release workspace: $childFull"
    }
}

function Get-SingleArtifact {
    param(
        [string]$ArtifactRoot,
        [string]$Filter
    )

    $files = @(Get-ChildItem -LiteralPath $ArtifactRoot -Recurse -File -Filter $Filter)
    if ($files.Count -ne 1) {
        throw "Expected exactly one $Filter artifact, found $($files.Count)."
    }

    $files[0]
}

function Assert-MinimalZipContainsNativeDictionary {
    param([string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName -ieq 'hoshidicts_c_api.dll' } |
            Select-Object -First 1

        if (-not $entry) {
            throw "Minimal zip is missing hoshidicts_c_api.dll: $ZipPath"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Wait-GitHubRelease {
    param(
        [string]$RepoName,
        [string]$Tag
    )

    $deadline = (Get-Date).AddMinutes(5)
    do {
        $json = & cmd /c "gh release view $Tag --repo $RepoName --json tagName,url,isDraft,isPrerelease,assets 2>NUL"
        if ($LASTEXITCODE -eq 0) {
            return ($json | Out-String | ConvertFrom-Json)
        }

        Write-Host "Waiting for GitHub Release $Tag..."
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for GitHub Release $Tag."
}

function Assert-ReleaseAssets {
    param(
        [object]$Release,
        [string]$Tag
    )

    $assetNames = @($Release.assets | ForEach-Object { [string]$_.name })

    if (-not ($assetNames -contains 'Niratan.Minimal.x64.zip')) {
        throw "GitHub Release $Tag is missing Niratan.Minimal.x64.zip. Assets: $($assetNames -join ', ')"
    }

    $setupAsset = $assetNames | Where-Object { $_ -like 'Niratan.Setup.x64*.exe' } | Select-Object -First 1
    if (-not $setupAsset) {
        throw "GitHub Release $Tag is missing Niratan.Setup.x64*.exe. Assets: $($assetNames -join ', ')"
    }
}

$normalized = ConvertTo-ReleaseVersion -RawVersion $Version
$plan = New-ReleasePlan `
    -ReleaseVersion $normalized.Version `
    -Tag $normalized.Tag `
    -RepoName $Repo `
    -BranchName $Branch `
    -WorkflowName $Workflow

if ($PlanOnly) {
    $plan | ConvertTo-Json -Depth 5
    return
}

if ($Draft -or $Prerelease -or $NotesFile) {
    throw 'Draft, Prerelease, and NotesFile options are not supported by the tag-driven GitHub Actions release flow.'
}

$repoRoot = if ($PSScriptRoot) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}

Push-Location $repoRoot
try {
    Assert-Command git
    Assert-Command gh

    Assert-CleanMain -BranchName $Branch

    Invoke-External git @('fetch', 'origin', $Branch, '--tags')
    $head = (Invoke-ExternalOutput git @('rev-parse', 'HEAD') | Out-String).Trim()
    $remoteHead = (Invoke-ExternalOutput git @('rev-parse', "origin/$Branch") | Out-String).Trim()
    if ($head -ne $remoteHead) {
        throw "$Branch must match origin/$Branch before release. Local: $head Remote: $remoteHead"
    }

    Assert-TagAvailable -Tag $normalized.Tag -RepoName $Repo

    $versionChanged = Set-ProjectVersion -RepoRoot $repoRoot -ReleaseVersion $normalized.Version
    if ($versionChanged) {
        Invoke-External git @('add', 'Niratan/Niratan.csproj')
        Invoke-External git @('commit', '-m', "chore: release $($normalized.Tag)")
    }
    else {
        Write-Host "Niratan.csproj already has version $($normalized.Version)."
    }

    Invoke-External git @('push', 'origin', $Branch)

    $releaseHead = (Invoke-ExternalOutput git @('rev-parse', 'HEAD') | Out-String).Trim()
    Invoke-External git @('tag', '-a', $normalized.Tag, '-m', "Release $($normalized.Tag)")
    Invoke-External git @('push', 'origin', $normalized.Tag)

    $runId = Wait-GitHubActionsRun `
        -RepoName $Repo `
        -WorkflowName $Workflow `
        -Tag $normalized.Tag `
        -HeadSha $releaseHead

    # Uses: gh run watch
    Invoke-External gh @('run', 'watch', $runId, '--repo', $Repo, '--exit-status')

    $release = Wait-GitHubRelease -RepoName $Repo -Tag $normalized.Tag
    Assert-ReleaseAssets -Release $release -Tag $normalized.Tag

    Write-Host "Release $($normalized.Tag) created by GitHub Actions: $($release.url)"
}
finally {
    Pop-Location
}
