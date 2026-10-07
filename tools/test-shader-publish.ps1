[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$run = Join-Path $repo ('artifacts\shader-publish-tests\' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $run 'publish'
[System.IO.Directory]::CreateDirectory($root) | Out-Null
$required = @(
    'EmbyNian.exe', 'EmbyNian.deps.json', 'EmbyNian.runtimeconfig.json', 'libmpv-2.dll', 'vulkan-1.dll',
    'EmbyNian.pri', 'mpv-ui\scripts\uosc\main.lua', 'mpv-ui\scripts\uosc\lib\utils.lua',
    'mpv-ui\scripts\uosc\elements\Timeline.lua', 'mpv-ui\fonts\uosc_textures.ttf',
    'mpv-ui\fonts\MaterialIconsRound-Regular.otf', 'mpv-ui\LICENSE.LGPL'
)
foreach ($relative in $required) {
    $target = Join-Path $root $relative
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    [System.IO.File]::WriteAllText($target, 'fixture themeresources')
}
$source = Join-Path $repo 'assets\shaders'
Copy-Item -LiteralPath $source -Destination (Join-Path $root 'shaders') -Recurse
$results = [System.Collections.Generic.List[object]]::new()
$check = Join-Path $PSScriptRoot 'verify-publish.ps1'

function Invoke-Check([string]$Name, [bool]$Success, [string]$Needle = '') {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $check -PublishRoot $root -RepositoryRoot $repo 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $saved }
    $text = $lines | Out-String
    $ok = (($code -eq 0) -eq $Success) -and ($Needle.Length -eq 0 -or $text.Contains($Needle))
    $results.Add([pscustomobject]@{ name=$Name; passed=$ok; exitCode=$code; output=$text })
    Write-Output ("{0}: {1}" -f $Name, $ok)
}

Invoke-Check 'complete payload' $true
$payload = @(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.Extension -in @('.glsl', '.hook', '.txt') })
foreach ($file in $payload) {
    $relative = $file.FullName.Substring($source.Length + 1)
    $target = Join-Path (Join-Path $root 'shaders') $relative
    $bytes = [System.IO.File]::ReadAllBytes($target)
    Remove-Item -LiteralPath $target
    Invoke-Check ('missing ' + $relative) $false $file.Name
    [System.IO.File]::WriteAllBytes($target, $bytes)
}
$first = $payload | Where-Object { $_.Extension -eq '.hook' } | Select-Object -First 1
$relative = $first.FullName.Substring($source.Length + 1)
$target = Join-Path (Join-Path $root 'shaders') $relative
$bytes = [System.IO.File]::ReadAllBytes($target)
[System.IO.File]::WriteAllText($target, 'damaged shader')
Invoke-Check 'corrupt hook' $false $first.Name
[System.IO.File]::WriteAllBytes($target, $bytes)
$extra = Join-Path $root 'shaders\unexpected.hook'
[System.IO.File]::WriteAllText($extra, 'unexpected')
Invoke-Check 'unexpected shader' $false 'unexpected.hook'
Remove-Item -LiteralPath $extra
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'results.json') -Encoding UTF8
$failed = @($results | Where-Object { -not $_.passed }).Count
Write-Output ("shader publish checks: {0} total, {1} failed; {2}" -f $results.Count, $failed, $run)
if ($failed -gt 0) { exit 1 }
exit 0
