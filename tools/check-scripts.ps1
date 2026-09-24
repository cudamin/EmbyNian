[CmdletBinding()]
param(
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path

if (-not $Path) {
    $relative = @(& git -C $repo -c core.quotepath=false ls-files --cached --others --exclude-standard -- '*.ps1')
    if ($LASTEXITCODE -ne 0) { throw '无法枚举当前工作树的 PowerShell 脚本。' }
    $Path = @($relative | Sort-Object -Unique | ForEach-Object { Join-Path $repo $_ })
}
if ($Path.Count -eq 0) { throw '没有可检查的 PowerShell 脚本。' }

$failures = [System.Collections.Generic.List[string]]::new()
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
foreach ($file in $Path) {
    $resolved = (Resolve-Path -LiteralPath $file).Path
    $bytes = [System.IO.File]::ReadAllBytes($resolved)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        $failures.Add("${resolved}：缺少 UTF-8 BOM。")
    }
    try {
        $text = $utf8.GetString($bytes).TrimStart([char]0xFEFF)
    } catch [System.Text.DecoderFallbackException] {
        $failures.Add("${resolved}：不是有效的 UTF-8。")
        continue
    }
    if ($text.Contains("`r")) { $failures.Add("${resolved}：含 CR 行尾，要求 LF。") }
    if (-not $text.EndsWith("`n")) { $failures.Add("${resolved}：缺少末尾换行。") }

    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
    foreach ($error in $errors) {
        $failures.Add("${resolved}:$($error.Extent.StartLineNumber)：$($error.Message)")
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output $failure }
    throw "PowerShell 检查失败：$($failures.Count) 个问题。"
}
Write-Output "PowerShell 检查通过：$($Path.Count) 个脚本，UTF-8 BOM、LF、末尾换行和语法均符合要求。"
