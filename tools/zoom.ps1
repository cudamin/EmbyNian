#requires -version 5
# 从一张截图里裁一块出来放大。判断 1 像素的发丝框、11 像素的等宽数字这种东西，1080 宽的整屏看不出来。
# 用最近邻放大：11 像素的字被双线性一插值就糊成灰块，而这里要看的正是它糊之前的样子。
param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$W,
    [Parameter(Mandatory = $true)][int]$H,
    [int]$Scale = 2
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile((Resolve-Path $In))
try {
    $x = [Math]::Max(0, [Math]::Min($X, $src.Width - 1))
    $y = [Math]::Max(0, [Math]::Min($Y, $src.Height - 1))
    $w = [Math]::Max(1, [Math]::Min($W, $src.Width - $x))
    $h = [Math]::Max(1, [Math]::Min($H, $src.Height - $y))

    $dst = New-Object System.Drawing.Bitmap ($w * $Scale), ($h * $Scale)
    $gfx = [System.Drawing.Graphics]::FromImage($dst)
    try {
        $gfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $gfx.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $gfx.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, ($w * $Scale), ($h * $Scale)),
            (New-Object System.Drawing.Rectangle $x, $y, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
    } finally { $gfx.Dispose() }

    $full = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Out))
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($full)) | Out-Null
    $dst.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
    Write-Host "$full $($w * $Scale)x$($h * $Scale)（原图 $x,$y $w×$h ×$Scale）"
} finally { $src.Dispose() }
