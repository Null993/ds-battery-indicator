param([string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts/sources'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$requests = @(
    @{ Repo = 'torvalds/linux'; Path = 'drivers/hid/hid-playstation.c' },
    @{ Repo = 'libsdl-org/SDL'; Path = 'src/joystick/hidapi/SDL_hidapi_ps5.c' },
    @{ Repo = 'nowrep/dualsensectl'; Path = 'main.c' },
    @{ Repo = 'nowrep/dualsensectl'; Path = 'README.md' },
    @{ Repo = 'nondebug/dualsense'; Path = 'README.md' },
    @{ Repo = 'nondebug/dualsense'; Path = 'dualsense-explorer.html' },
    @{ Repo = 'nondebug/dualsense'; Path = 'report-descriptor-usb.txt' },
    @{ Repo = 'nondebug/dualsense'; Path = 'report-descriptor-bluetooth.txt' },
    @{ Repo = 'Paliverse/DualSense-List-of-Firmwares'; Path = 'README.md' },
    @{ Repo = 'dualshock-tools/ds4-tools'; Path = 'ds5-calibration-tool.py' },
    @{ Repo = 'dualshock-tools/ds4-tools'; Path = 'README.md' },
    @{ Repo = 'nsfm/dualsense-ts'; Path = 'src/hid/battery_state.ts' },
    @{ Repo = 'nsfm/dualsense-ts'; Path = 'src/hid/command.ts' },
    @{ Repo = 'nsfm/dualsense-ts'; Path = 'src/hid/factory_info.ts' },
    @{ Repo = 'nsfm/dualsense-ts'; Path = 'src/hid/dsp.ts' },
    @{ Repo = 'nsfm/dualsense-ts'; Path = 'src/hid/dualsense_hid.ts' },
    @{ Repo = 'olliejudge/dsmactools'; Path = 'src/firmware.rs' },
    @{ Repo = 'olliejudge/dsmactools'; Path = 'src/protocol.rs' }
)
$oldHttp = $env:HTTP_PROXY
$oldHttps = $env:HTTPS_PROXY
$manifest = [Collections.Generic.List[object]]::new()
try {
    # gh has no proxy flag. Scope these variables to this script and restore them below.
    $env:HTTP_PROXY = 'http://127.0.0.1:7890'
    $env:HTTPS_PROXY = 'http://127.0.0.1:7890'
    foreach ($request in $requests) {
        $commitJson = & gh api "repos/$($request.Repo)/commits?path=$($request.Path)&per_page=1"
        if ($LASTEXITCODE -ne 0) { throw "Commit lookup failed: $($request.Repo)/$($request.Path)" }
        $commit = ($commitJson | ConvertFrom-Json)[0].sha
        if ($commit -notmatch '^[a-f0-9]{40}$') { throw 'Invalid commit identifier' }
        $name = ($request.Repo + '/' + $request.Path) -replace '/', '__'
        $url = "https://raw.githubusercontent.com/$($request.Repo)/$commit/$($request.Path)"
        $file = Join-Path $OutputRoot $name
        & curl.exe --proxy http://127.0.0.1:7890 --fail --location --max-time 45 --silent --show-error $url --output $file
        if ($LASTEXITCODE -ne 0) { throw "Source download failed: $url" }
        $manifest.Add([pscustomobject]@{
            repo = $request.Repo; path = $request.Path; commit = $commit; url = $url
            retrievedUtc = [DateTimeOffset]::UtcNow.ToString('o'); localFile = $name
            bytes = (Get-Item -LiteralPath $file).Length
            sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        })
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $OutputRoot 'manifest.json')
        Write-Output "Saved $name at $commit"
    }
} finally {
    $env:HTTP_PROXY = $oldHttp
    $env:HTTPS_PROXY = $oldHttps
}
