[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourceRoot = 'D:\smb',

    [Parameter()]
    [string]$DestinationRoot = 'D:\smb-organized',

    [Parameter()]
    [ValidateSet('Plan', 'HardLink', 'Copy')]
    [string]$Mode = 'Plan',

    [Parameter()]
    [string]$ManifestPath = '.\video-library-organize-manifest.csv'
)

$ErrorActionPreference = 'Stop'

function Get-NormalizedPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Test-PathInside([string]$Child, [string]$Parent) {
    $childPath = (Get-NormalizedPath $Child) + '\'
    $parentPath = (Get-NormalizedPath $Parent) + '\'
    return $childPath.StartsWith($parentPath, [StringComparison]::OrdinalIgnoreCase)
}

function Remove-ReleaseNoise([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return 'Unknown'
    }

    $clean = $Value
    $clean = [Regex]::Replace($clean, '^\[[^\]]+\]\s*', '')
    $clean = [Regex]::Replace($clean, '\[[^\]]+\]', ' ')
    $clean = [Regex]::Replace($clean, '\((?:19|20)\d{2}\)', ' ')
    $clean = [Regex]::Replace($clean, '(?i)\b(?:BDRip|BDrip|WEB[- ]?DL|WEBRip|HEVC|AV1|x264|x265|AAC|FLAC|OPUS|1080P|720P|2160P|全集|TV全集|简繁外挂)\b', ' ')
    $clean = $clean -replace '[\u3010\u3011]', ' '
    $clean = [Regex]::Replace($clean, '\s+', ' ').Trim(' ', '.', '-', '_', '[', ']')
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return 'Unknown'
    }
    return $clean
}

function Resolve-SeriesTitle([IO.FileInfo]$File, [string]$RelativePath) {
    $haystack = $RelativePath.ToLowerInvariant()
    if ($haystack -match 'mushoku|無職転生|無職轉生') {
        return 'Mushoku Tensei Isekai Ittara Honki Dasu'
    }
    if ($haystack -match 're0|re[_：: -]?zero|re zero|re：從零|re：从零|異世界生活|异世界生活') {
        return 'Re Zero kara Hajimeru Isekai Seikatsu'
    }
    if ($haystack -match 'himouto|himoto|うまる') {
        return 'Himouto Umaru-chan'
    }

    $parent = Split-Path -Parent $RelativePath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        $parts = $parent -split '[\\/]'
        for ($i = $parts.Count - 1; $i -ge 0; $i--) {
            $candidate = Remove-ReleaseNoise $parts[$i]
            if ($candidate -notmatch '^(?i:season|specials?|extras?|pv|menu|ncop|nced|re0|himoto)$' -and $candidate.Length -ge 3) {
                return $candidate
            }
        }
    }

    return Remove-ReleaseNoise ([IO.Path]::GetFileNameWithoutExtension($File.Name))
}

function Resolve-SeasonNumber([string]$RelativePath, [string]$SeriesTitle) {
    if ($RelativePath -match '(?i)\b(?:specials?|extras?|pv|menu|ncop|nced|mini)\b|迷你动画|(?:^|[\\/])NC(?:OP|ED)\d*\.mkv$') {
        return 0
    }
    if ($RelativePath -match '(?i)S(?<season>\d{1,2})E\d{1,3}') {
        return [int]$Matches['season']
    }
    if ($RelativePath -match '(?i)Season[ ._-]?(?<season>\d{1,2})') {
        return [int]$Matches['season']
    }
    if ($RelativePath -match '第(?<season>\d{1,2})季') {
        return [int]$Matches['season']
    }
    if ($RelativePath -match '(?i)\bS(?<season>\d{1,2})\b') {
        return [int]$Matches['season']
    }
    if ($SeriesTitle -eq 'Himouto Umaru-chan') {
        return 1
    }
    return $null
}

function Resolve-EpisodeNumber([string]$RelativePath, [string]$FileName) {
    if ($RelativePath -match '(?i)S\d{1,2}E(?<episode>\d{1,3})') {
        return [int]$Matches['episode']
    }
    if ($RelativePath -match '(?i)\bE(?<episode>\d{1,3})\b') {
        return [int]$Matches['episode']
    }

    $bracketMatches = [Regex]::Matches($FileName, '\[(?<episode>\d{1,3})\]')
    if ($bracketMatches.Count -gt 0) {
        return [int]$bracketMatches[$bracketMatches.Count - 1].Groups['episode'].Value
    }
    if ($FileName -match '(?i)(?:^|[- _])(?<episode>\d{1,3})(?:[- _]|\[|\.|$)') {
        return [int]$Matches['episode']
    }
    return $null
}

function Get-RelativePath([string]$Root, [string]$Path) {
    return $Path.Substring($Root.Length).TrimStart('\', '/')
}

function Get-TargetName([string]$SeriesTitle, [Nullable[int]]$Season, [Nullable[int]]$Episode, [IO.FileInfo]$File) {
    $extension = $File.Extension.ToLowerInvariant()
    if ($null -ne $Season -and $null -ne $Episode) {
        return ('{0} - S{1:D2}E{2:D2}{3}' -f $SeriesTitle, $Season, $Episode, $extension)
    }
    return ((Remove-ReleaseNoise ([IO.Path]::GetFileNameWithoutExtension($File.Name))) + $extension)
}

function Add-PlanItem([System.Collections.Generic.List[object]]$Items, [IO.FileInfo]$File, [string]$Source, [string]$Destination) {
    $Items.Add([pscustomobject]@{
        Source = $File.FullName
        Destination = $Destination
        Bytes = $File.Length
        Length = $File.Length
        Kind = 'video'
    }) | Out-Null

    $sidecarExtensions = @('.srt', '.ass', '.ssa', '.nfo', '.jpg', '.jpeg', '.png')
    foreach ($extension in $sidecarExtensions) {
        $sidecar = Join-Path $File.DirectoryName ($File.BaseName + $extension)
        if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
            $sidecarFile = Get-Item -LiteralPath $sidecar
            $sidecarDestination = [IO.Path]::ChangeExtension($Destination, $extension)
            $Items.Add([pscustomobject]@{
                Source = $sidecarFile.FullName
                Destination = $sidecarDestination
                Bytes = $sidecarFile.Length
                Length = $sidecarFile.Length
                Kind = 'sidecar'
            }) | Out-Null
        }
    }
}

$source = Get-NormalizedPath $SourceRoot
$destination = Get-NormalizedPath $DestinationRoot
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Source directory does not exist: $source"
}
if ([StringComparer]::OrdinalIgnoreCase.Equals($source, $destination)) {
    throw 'Destination must be different from the source directory.'
}
if (Test-PathInside $destination $source) {
    throw 'Destination cannot be inside the source directory.'
}

$videoExtensions = @('.mkv', '.mp4', '.m4v', '.avi', '.webm')
$videoFiles = @(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object {
    $videoExtensions -contains $_.Extension.ToLowerInvariant()
})
if ($videoFiles.Count -eq 0) {
    throw "No video files found under $source"
}

$items = [System.Collections.Generic.List[object]]::new()
foreach ($file in $videoFiles) {
    $relative = Get-RelativePath $source $file.FullName
    $series = Resolve-SeriesTitle $file $relative
    $season = Resolve-SeasonNumber $relative $series
    $episode = Resolve-EpisodeNumber $relative $file.Name
    $seriesFolder = $series
    if ($null -eq $season) {
        $seriesFolder = 'Movies'
    }
    elseif ($season -eq 0) {
        $seriesFolder = Join-Path $series 'Specials'
    }
    else {
        $seriesFolder = Join-Path $series ('Season {0:D2}' -f $season)
    }
    $targetName = Get-TargetName $series $season $episode $file
    $targetPath = Join-Path (Join-Path $destination $seriesFolder) $targetName

    if ($items | Where-Object { $_.Destination -eq $targetPath }) {
        $relativeHash = [Math]::Abs($relative.GetHashCode())
        $targetPath = Join-Path (Split-Path -Parent $targetPath) (('{0} - {1}{2}' -f [IO.Path]::GetFileNameWithoutExtension($targetName), $relativeHash, $file.Extension.ToLowerInvariant()))
    }
    Add-PlanItem $items $file $source $targetPath
}

$manifestDirectory = Split-Path -Parent (Get-NormalizedPath $ManifestPath)
if (-not [string]::IsNullOrWhiteSpace($manifestDirectory) -and -not (Test-Path -LiteralPath $manifestDirectory)) {
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
}
$items | Select-Object Source,Destination,Bytes,Kind | Export-Csv -LiteralPath $ManifestPath -NoTypeInformation -Encoding UTF8

$videoItems = @($items | Where-Object Kind -eq 'video')
$totalBytes = ($items | Measure-Object -Property Bytes -Sum).Sum
Write-Host ("Planned {0} videos and {1} sidecars ({2:N2} GB)." -f $videoItems.Count, ($items.Count - $videoItems.Count), ($totalBytes / 1GB))
Write-Host "Manifest: $([IO.Path]::GetFullPath($ManifestPath))"

if ($Mode -eq 'Plan') {
    $items | Group-Object Destination | Select-Object -First 20 | ForEach-Object { Write-Host $_.Name }
    exit 0
}

if (Test-Path -LiteralPath $destination) {
    $existing = @(Get-ChildItem -LiteralPath $destination -Force -Recurse -ErrorAction Stop)
    if ($existing.Count -gt 0) {
        throw "Destination is not empty: $destination"
    }
}
else {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
}

foreach ($item in $items) {
    $parent = Split-Path -Parent $item.Destination
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    if ($Mode -eq 'Copy') {
        Copy-Item -LiteralPath $item.Source -Destination $item.Destination -Force
    }
    else {
        & fsutil.exe hardlink create $item.Destination $item.Source | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create hard link: $($item.Destination)"
        }
    }
}

Write-Host ("Organized {0} files into {1}. Source files were not moved, renamed, or deleted." -f $items.Count, $destination)
