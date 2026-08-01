[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedIcon,

    [Parameter(Mandatory = $true)]
    [string]$TargetExecutable,

    [ValidateRange(16, 256)]
    [int]$Size = 32
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function Get-RenderedIconHash {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Icon]$Icon,

        [Parameter(Mandatory = $true)]
        [int]$RenderSize
    )

    $bitmap = [System.Drawing.Bitmap]::new($RenderSize, $RenderSize)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawIcon($Icon, 0, 0)
    }
    finally {
        $graphics.Dispose()
    }

    $stream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $stream.Position = 0
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            [Convert]::ToHexString($sha256.ComputeHash($stream))
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

$expectedPath = (Resolve-Path -LiteralPath $ExpectedIcon).Path
$targetPath = (Resolve-Path -LiteralPath $TargetExecutable).Path
$expected = [System.Drawing.Icon]::new($expectedPath, $Size, $Size)
$actual = [System.Drawing.Icon]::ExtractAssociatedIcon($targetPath)

if (-not $actual) {
    $expected.Dispose()
    throw "No executable icon found in $targetPath"
}

try {
    $expectedHash = Get-RenderedIconHash -Icon $expected -RenderSize $Size
    $actualHash = Get-RenderedIconHash -Icon $actual -RenderSize $Size

    if ($actualHash -ne $expectedHash) {
        throw "Packaged icon does not match $expectedPath at ${Size}x${Size}: $targetPath"
    }

    Write-Host "Packaged icon matches $expectedPath at ${Size}x${Size}: $targetPath"
}
finally {
    $expected.Dispose()
    $actual.Dispose()
}
