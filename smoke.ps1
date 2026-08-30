# Launches a build, waits, then reports whether it survived and what the log says.
# Used as the smoke-test loop while finishing the rewrite: a crash in a control constructor cannot
# be caught by the compiler or the unit tests. --self-check covers the pages; this covers the one
# thing it deliberately skips, the real sign-in and first load.
#
#   .\smoke.ps1                  WinUI 3 调试版
#   .\smoke.ps1 -Release         artifacts\publish\win-x64 里的发行版
param(
    [int]$Seconds = 12,
    [switch]$Release,
    [string]$Exe
)

if (-not $Exe) {
    # x64 rather than AnyCPU: WinUI 3 has no AnyCPU, and the csproj pins Platform.
    $Exe = if ($Release) {
        Join-Path $PSScriptRoot 'artifacts\publish\win-x64\EmbyNian.exe'
    } else {
        Join-Path $PSScriptRoot 'src\EmbyNian.Shell\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\EmbyNian.exe'
    }
}

if (-not (Test-Path $Exe)) {
    Write-Output ("NOT FOUND {0}" -f $Exe)
    exit 1
}

Write-Output ("启动 {0}" -f $Exe)
$log = Join-Path $env:LOCALAPPDATA ("EmbyNian\logs\app-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))

$before = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }

$process = Start-Process $Exe -PassThru
Start-Sleep -Seconds $Seconds

if ($process.HasExited) {
    Write-Output ("EXITED code={0}" -f $process.ExitCode)
} else {
    Write-Output ("RUNNING pid={0}" -f $process.Id)
    Stop-Process -Id $process.Id -Force
}

if (Test-Path $log) {
    $stream = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    $stream.Seek($before, 'Begin') | Out-Null
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd()
    $reader.Close()
    Write-Output '--- 本次运行的日志 ---'
    Write-Output $text
}
