$ErrorActionPreference = "Stop"

chcp 65001 | Out-Null

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom
$global:OutputEncoding = $utf8NoBom

Write-Host "PowerShell console encoding set to UTF-8 (code page 65001)."
