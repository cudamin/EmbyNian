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

# EmbyNian.pri 必须把框架那几份 .pri 并进来，否则程序在 App.xaml 那一步就崩：
# 「Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'」。
#
# 2026-09-05 的第一版归因是错的（写成「第二次发布就好」），2026-09-11 挖到底：并进来这一步的输入是 obj 里的
# pri.resfiles，它来自 @(_PriFilesFromPayload)，而那个变体读的是 **@(PriOutputs)** —— 由 GetPriOutputs 填，
# 只含项目自己的 xbf/内容加 ProjectReference。框架那三份（Microsoft.UI.Xaml.Controls.pri 在 WinUI 包里、
# Microsoft.UI.pri 在 InteractiveExperiences 包里、Microsoft.WindowsAppRuntime.pri 在 Foundation 包里）
# 走的是 runtimes-framework 那套 NuGet 资产，**从来不进 PriOutputs**，所以 pri.resfiles 恒为 0 字节、
# EmbyNian.pri 只有 103~135 KB（并进来是 2.2 MB），发布件少 2 MB，程序一启动就死。
#
# **跟「清没清过 bin」「跑第几次」无关**：2026-09-11 对着刚清过的 bin 连跑四次发布，四次都是 135 KB，
# 没有一次「第二次就好」。修法在 Shell 项目的 csproj 里（AddWindowsAppSdkFrameworkPriToPayload 目标，
# 把三份框架 .pri 手动塞进 PriOutputs），不是在这里重跑。
#
# 谁都不会去数 EmbyNian.pri 有多大，而这个坏法的唯一症状是「程序打不开」，所以这一关在这里守。
# 判据是它索引里有没有 themeresources 这个名字，不是大小 —— 大小会随 XAML 增减浮动，那个名字不会。
$appPri = Join-Path $root 'EmbyNian.pri'
Require-File $appPri '应用资源索引'
$priBytes = [System.IO.File]::ReadAllBytes($appPri)
# GetEncoding(28591) 而不是 [Encoding]::Latin1：这个脚本跑在 Windows PowerShell 5.1（.NET Framework）上，
# 那里没有 Latin1 这个静态属性。要的只是「一个字节一个字符」，好在二进制里找一个 ASCII 名字。
$priText = [System.Text.Encoding]::GetEncoding(28591).GetString($priBytes)
if ($priText -notmatch 'themeresources') {
    $kb = [math]::Round($priBytes.Length / 1KB)
    throw ("发布验证失败：EmbyNian.pri（$kb KB）里没有框架的 themeresources，程序启动时会在 App.xaml 崩掉。" +
        "原因是框架那三份 .pri 从来没被并进来（PriOutputs 里没有它们）。修法在 Shell 项目的 csproj 里：" +
        "AddWindowsAppSdkFrameworkPriToPayload 目标。**重跑这个发布脚本没用** —— 2026-09-11 连跑四次都是这个大小。")
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
