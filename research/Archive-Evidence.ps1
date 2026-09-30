param([string]$Destination = (Join-Path $PSScriptRoot '../publish/DualSense-research-2026-09-30.zip'))
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'artifacts'
$items = Get-ChildItem -LiteralPath $root | Where-Object { $_.Name -notin @('tools', 'python-libs', 'evidence-manifest.json') }
$files = foreach ($item in $items) {
    if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $item.FullName -File -Recurse }
    else { $item }
}
$manifest = foreach ($file in ($files | Sort-Object FullName)) {
    [pscustomobject]@{
        path = [IO.Path]::GetRelativePath($root, $file.FullName)
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $root 'evidence-manifest.json')
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
$paths = @($items.FullName) + @(Join-Path $root 'evidence-manifest.json')
Compress-Archive -LiteralPath $paths -DestinationPath $Destination -Force
Get-FileHash -LiteralPath $Destination -Algorithm SHA256
# Tool installations are excluded; source, captures, firmware, static outputs and previews are included.
