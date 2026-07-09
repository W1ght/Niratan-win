Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseScript = Join-Path $repoRoot 'release.ps1'

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-PowerShellExecutable {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh) {
        return $pwsh.Source
    }

    return (Get-Command powershell -ErrorAction Stop).Source
}

Assert-True (Test-Path -LiteralPath $releaseScript) 'release.ps1 must exist at the repository root.'

$powershell = Get-PowerShellExecutable
$planOutput = & $powershell -NoProfile -ExecutionPolicy Bypass -File $releaseScript -Version v1.2.3 -PlanOnly
if ($LASTEXITCODE -ne 0) {
    throw "release.ps1 -PlanOnly failed with exit code $LASTEXITCODE."
}

$plan = ($planOutput | Out-String | ConvertFrom-Json)
Assert-True ($plan.Version -eq '1.2.3') 'Plan should normalize the numeric version.'
Assert-True ($plan.Tag -eq 'v1.2.3') 'Plan should normalize the git tag.'
Assert-True ($plan.Branch -eq 'main') 'Plan should release from main by default.'
Assert-True ($plan.Workflow -eq 'CI - Build and Package') 'Plan should use the packaging workflow.'
Assert-True ($plan.RunsLocalBuildOrTests -eq $false) 'Release script must not run local build or test commands.'
Assert-True (($plan.Steps -join "`n") -match 'create the release') 'Plan should rely on GitHub Actions to create the release.'
Assert-True (($plan.Steps -join "`n") -match 'Validate GitHub Release assets') 'Plan should validate Release assets.'

$scriptContent = Get-Content -LiteralPath $releaseScript -Raw -Encoding UTF8
Assert-True ($scriptContent -notmatch 'dotnet\s+(build|test)') 'Release script must not run local dotnet build/test.'
Assert-True ($scriptContent -match 'hoshidicts_c_api\.dll') 'Release script must validate the native dictionary DLL.'
Assert-True ($scriptContent -match 'gh\s+run\s+watch') 'Release script must wait for the GitHub Actions run.'
Assert-True ($scriptContent -match 'gh\s+release\s+view') 'Release script must inspect the GitHub Release created by Actions.'
Assert-True ($scriptContent -notmatch "'run',\s*'download'") 'Release script must not download Actions artifacts locally.'
Assert-True ($scriptContent -notmatch "'release',\s*'create'") 'Release script must not create/upload the Release from the local machine.'
Assert-True ($scriptContent -notmatch '\*>\s*\$null') 'Release script must not let native stderr become a PowerShell NativeCommandError during existence checks.'

$workflowPath = Join-Path $repoRoot '.github\workflows\build-and-package.yml'
Assert-True (Test-Path -LiteralPath $workflowPath) 'Packaging workflow must exist.'
$workflowContent = Get-Content -LiteralPath $workflowPath -Raw -Encoding UTF8
Assert-True ($workflowContent -match 'contents:\s*write') 'Packaging workflow must have release write permission.'
Assert-True ($workflowContent -match 'dotnet\s+test') 'Packaging workflow must run tests before release.'
Assert-True ($workflowContent -match 'hoshidicts_c_api\.dll') 'Packaging workflow must verify the native dictionary DLL before release.'
Assert-True ($workflowContent -match 'softprops/action-gh-release@v2') 'Packaging workflow must create the GitHub Release directly.'

Write-Host 'Release script behavior checks passed.'
