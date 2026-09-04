[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,

    [switch]$SelfContained,

    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "发布验证失败：缺少 $Description：$Path"
    }
}

$root = (Resolve-Path -LiteralPath $PublishRoot).Path
Require-File (Join-Path $root 'EmbyNian.exe') '主程序'
Require-File (Join-Path $root 'EmbyNian.deps.json') '依赖清单'
Require-File (Join-Path $root 'EmbyNian.runtimeconfig.json') '运行时配置'
Require-File (Join-Path $root 'libmpv-2.dll') '内置 libmpv'
# libmpv-2.dll 的静态导入里唯一一个不属于 Windows 的 dll（2026-09-04 起来自仓库的 assets\mpv-runtime，
# 从前是从用户自己那套便携版 mpv 里现拷的）。缺了它 libmpv 连加载都失败，一帧都放不出来 —— 而四道闸门
# 里没有一关会真的起播，所以这一行是它唯一的守卫。
Require-File (Join-Path $root 'vulkan-1.dll') 'libmpv 依赖的 Vulkan loader'

$shaderCount = @(Get-ChildItem -LiteralPath (Join-Path $root 'shaders') -Recurse -Filter '*.glsl' -File -ErrorAction SilentlyContinue).Count
if ($shaderCount -eq 0) {
    throw "发布验证失败：shaders 目录为空。着色器在仓库的 assets\shaders 里，由 Shell 项目的 csproj 拷进输出目录，先看这两处。"
}

if ($SelfContained) {
    Require-File (Join-Path $root 'Microsoft.WindowsAppRuntime.Bootstrap.dll') 'Windows App SDK 自包含运行时'
    Require-File (Join-Path $root 'hostfxr.dll') '.NET 自包含运行时'
}

$solution = Join-Path (Resolve-Path -LiteralPath $RepositoryRoot).Path 'EmbyNian.sln'
$legacyApp = Join-Path (Resolve-Path -LiteralPath $RepositoryRoot).Path 'src\EmbyNian.App'
if (Test-Path -LiteralPath $legacyApp) {
    throw "发布验证失败：迁移期 WinForms 目录仍存在：$legacyApp"
}
if (Test-Path -LiteralPath $solution -PathType Leaf) {
    $solutionText = Get-Content -LiteralPath $solution -Raw
    if ($solutionText -match 'EmbyNian\.App|src[\\/]EmbyNian\.App') {
        throw "发布验证失败：解决方案仍包含已移除的 WinForms 外壳。"
    }
}

$files = @(Get-ChildItem -LiteralPath $root -Recurse -File)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Output ("发布验证通过：{0} 个文件，{1:N1} MB，{2} 个 GLSL 着色器。" -f $files.Count, ($bytes / 1MB), $shaderCount)
