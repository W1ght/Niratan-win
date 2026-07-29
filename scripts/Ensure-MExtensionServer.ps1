[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [string]$Version = '1.0.4',

    [string]$SourceCommit = 'e86c8c627a628f71b52cfa70cd1435cacc96c190',

    [string]$ArchiveSha256 = '4bdd8e068914a769b4ff132080210d2a8be806e9c401a577dd700cb662a302ee',

    [string]$OverlaySha256 = 'edf198c73f7ffa54e356396833d4c0a34d86366cd59aa0edae9d1559e7960d7c'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Parent,

        [Parameter(Mandatory = $true)]
        [string]$Child
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $childFull = [System.IO.Path]::GetFullPath($Child).TrimEnd('\', '/')
    $requiredPrefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $childFull.StartsWith(
            $requiredPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the M-Extension-Server cache: $childFull"
    }
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Parent,

        [Parameter(Mandatory = $true)]
        [string]$Child
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + '\'
    $childFull = [System.IO.Path]::GetFullPath($Child)
    $parentUri = New-Object System.Uri($parentFull)
    $childUri = New-Object System.Uri($childFull)
    [System.Uri]::UnescapeDataString(
        $parentUri.MakeRelativeUri($childUri).ToString()
    )
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return [System.BitConverter]::ToString(
                $sha256.ComputeHash($stream)
            ).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$repositoryFull = [System.IO.Path]::GetFullPath($RepositoryRoot)
$artifactsRoot = Join-Path $repositoryFull 'artifacts\MExtensionServer'
$versionRoot = Join-Path $artifactsRoot $Version
$runtimeRoot = Join-Path $versionRoot 'runtime'
$manifestPath = Join-Path $runtimeRoot 'runtime.json'
$archivePath = Join-Path $versionRoot 'windows-x64-bundle.zip'
$archiveUrl =
    "https://github.com/kodjodevf/M-Extension-Server/releases/download/v$Version/windows-x64-bundle.zip"
$noticePath = Join-Path $repositoryFull 'ThirdParty\MExtensionServer\NOTICE.txt'
$overlaySourcePath = Join-Path $repositoryFull `
    'ThirdParty\MExtensionServer\overlay\NiratanMExtensionOverlay.jar'
$overlayFileName = 'NiratanMExtensionOverlay.jar'

if (-not (Test-Path -LiteralPath $overlaySourcePath)) {
    throw "Niratan M-Extension-Server overlay is missing: $overlaySourcePath"
}
$actualOverlayHash = Get-Sha256 -Path $overlaySourcePath
if ($actualOverlayHash -ne $OverlaySha256) {
    throw "Niratan M-Extension-Server overlay SHA-256 mismatch. Expected $OverlaySha256, received $actualOverlayHash."
}

Assert-ChildPath -Parent $artifactsRoot -Child $versionRoot
Assert-ChildPath -Parent $artifactsRoot -Child $runtimeRoot

if (Test-Path -LiteralPath $manifestPath) {
    $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
    $manifestHasUtf8Bom =
        ($manifestBytes.Length -ge 3) -and
        ($manifestBytes[0] -eq 0xEF) -and
        ($manifestBytes[1] -eq 0xBB) -and
        ($manifestBytes[2] -eq 0xBF)
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $javaPath = Join-Path $runtimeRoot $manifest.javaExecutable
    $jarPath = Join-Path $runtimeRoot $manifest.serverJar
    $overlayJarProperty = $manifest.PSObject.Properties['overlayJar']
    $overlayHashProperty = $manifest.PSObject.Properties['overlaySha256']
    $overlayPath = if ($overlayJarProperty) {
        Join-Path $runtimeRoot $overlayJarProperty.Value
    } else {
        ''
    }
    if (($manifest.schemaVersion -eq 2) -and
        ($manifest.version -eq $Version) -and
        ($manifest.archiveSha256 -eq $ArchiveSha256) -and
        (-not $manifestHasUtf8Bom) -and
        $overlayHashProperty -and
        ($overlayHashProperty.Value -eq $OverlaySha256) -and
        (Test-Path -LiteralPath $javaPath) -and
        (Test-Path -LiteralPath $jarPath) -and
        (Test-Path -LiteralPath $overlayPath) -and
        ((Get-Sha256 -Path $overlayPath) -eq $OverlaySha256) -and
        (Test-Path -LiteralPath (Join-Path $runtimeRoot 'LICENSE-MPL-2.0.txt')) -and
        (Test-Path -LiteralPath (Join-Path $runtimeRoot 'NOTICE.txt'))) {
        Write-Host "M-Extension-Server $Version is ready: $runtimeRoot"
        return
    }
}

New-Item -ItemType Directory -Force -Path $versionRoot | Out-Null

$archiveIsValid = $false
if (Test-Path -LiteralPath $archivePath) {
    $actualHash = Get-Sha256 -Path $archivePath
    $archiveIsValid = $actualHash -eq $ArchiveSha256
}

if (-not $archiveIsValid) {
    $partialPath = $archivePath + '.partial'
    Assert-ChildPath -Parent $artifactsRoot -Child $partialPath
    if (Test-Path -LiteralPath $partialPath) {
        Remove-Item -LiteralPath $partialPath -Force
    }

    Write-Host "Downloading bundled M-Extension-Server $Version..."
    & curl.exe `
        -L `
        --fail `
        --retry 3 `
        --output $partialPath `
        $archiveUrl
    if ($LASTEXITCODE -ne 0) {
        throw "M-Extension-Server download failed with exit code $LASTEXITCODE."
    }

    $actualHash = Get-Sha256 -Path $partialPath
    if ($actualHash -ne $ArchiveSha256) {
        Remove-Item -LiteralPath $partialPath -Force
        throw "M-Extension-Server archive SHA-256 mismatch. Expected $ArchiveSha256, received $actualHash."
    }
    Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
}

$stageRoot = Join-Path $versionRoot ("stage-" + [Guid]::NewGuid().ToString('N'))
Assert-ChildPath -Parent $artifactsRoot -Child $stageRoot
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stageRoot

    $javaFiles = @(
        Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Filter 'java.exe' |
            Where-Object { $_.Directory.Name -ieq 'bin' }
    )
    if ($javaFiles.Count -ne 1) {
        throw "Expected one bundled java.exe, found $($javaFiles.Count)."
    }

    $jarFiles = @(
        Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Filter 'MExtensionServer-*.jar'
    )
    if ($jarFiles.Count -ne 1) {
        throw "Expected one M-Extension-Server JAR, found $($jarFiles.Count)."
    }
    Copy-Item -LiteralPath $overlaySourcePath `
        -Destination (Join-Path $stageRoot $overlayFileName)

    $licenseUrl =
        "https://raw.githubusercontent.com/kodjodevf/M-Extension-Server/$SourceCommit/LICENSE"
    $licensePath = Join-Path $stageRoot 'LICENSE-MPL-2.0.txt'
    & curl.exe -L --fail --retry 3 --output $licensePath $licenseUrl
    if ($LASTEXITCODE -ne 0) {
        throw "M-Extension-Server license download failed with exit code $LASTEXITCODE."
    }
    Copy-Item -LiteralPath $noticePath -Destination (Join-Path $stageRoot 'NOTICE.txt')

    $manifest = [ordered]@{
        schemaVersion = 2
        version = $Version
        sourceCommit = $SourceCommit
        archiveSha256 = $ArchiveSha256
        overlaySha256 = $OverlaySha256
        javaExecutable = Get-RelativePath -Parent $stageRoot -Child $javaFiles[0].FullName
        serverJar = Get-RelativePath -Parent $stageRoot -Child $jarFiles[0].FullName
        overlayJar = $overlayFileName
    }
    $manifestJson = $manifest | ConvertTo-Json
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $stageRoot 'runtime.json'),
        $manifestJson,
        $utf8NoBom)

    if (Test-Path -LiteralPath $runtimeRoot) {
        Assert-ChildPath -Parent $artifactsRoot -Child $runtimeRoot
        Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
    }
    Move-Item -LiteralPath $stageRoot -Destination $runtimeRoot
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Assert-ChildPath -Parent $artifactsRoot -Child $stageRoot
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}

Write-Host "M-Extension-Server $Version is ready: $runtimeRoot"
