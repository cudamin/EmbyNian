# 着色器文件的来处

这十五个文件是 2026-09-03 从上游取下来入库的（当时是二十一个 —— Anime4K Mode A 那六个 2026-09-04
出箱了，见下面「出过箱的文件」）。**从前它们不在仓库里** —— 发布脚本从
`C:\mpv_config-2026.08.12\portable_config\shaders` 现拷，也就是说这个程序发不发得出正确的画质，
取决于另一个软件的安装目录还在不在。入库之后 `src/EmbyNian.Shell/EmbyNian.Shell.csproj` 从这里拷，
清单由单测钉着不许和 `src/EmbyNian.Core/Mpv/ShaderGroupCatalog.cs` 里的档位表跑偏 —— 两个方向都钉：
表点了一个磁盘上没有的文件是一种错，仓库里躺着一个没有任何档位用得上的文件是另一种。

改动过的只有一处，见下面 `igv/adaptive-sharpen.glsl` 那一行。其余每个文件都和上游一字节不差。

| 目录 | 上游 | 许可 |
|---|---|---|
| `ArtCNN/` | [Artoriuz/ArtCNN](https://github.com/Artoriuz/ArtCNN) 的 `GLSL/` | MIT |
| `CfL/` | [Artoriuz/glsl-chroma-from-luma-prediction](https://github.com/Artoriuz/glsl-chroma-from-luma-prediction) | MIT |
| `igv/` | igv 的三个 gist（见下） | LGPL-3.0（SSim 两个）、BSD-2（adaptive-sharpen） |
| `ravu/` | [bjin/mpv-prescalers](https://github.com/bjin/mpv-prescalers) 根目录 | LGPL-3.0 |
| `an3223/` | [AN3223/dotfiles](https://github.com/AN3223/dotfiles) 的 `.config/mpv/shaders` | LGPL-2.1 |

igv 那三个的 gist 地址（更新时按这个取）：

- `SSimDownscaler.glsl` — <https://gist.github.com/igv/36508af3ffc84410fe39761d6969be10>
- `SSimSuperRes.glsl` — <https://gist.github.com/igv/2364ffa6e81540f29cb7ab4c9bc05b6b>
- `adaptive-sharpen.glsl` — <https://gist.github.com/igv/8a77e4eb8276753b54bb94c1c50c317e>

## 唯一一处本地改动

`igv/adaptive-sharpen.glsl` 第 34 行 `#define curve_height` 从上游的 `1.0` 改成 `0.5`。
理由：igv 自己的 gist 说明写的推荐强度就是 0.5，而这个数是编译期 `#define`，mpv 没有对应的运行时选项，
不改文件就没有第二个办法。更新这个文件时要把这一行重新改回 0.5。

## 扩展名

`ravu/` 那四个是 `.hook`，那是上游的原名 —— 从前仓库里是被改名成 `.glsl` 的
（`ravu-zoom-ar-r2.glsl`）。mpv 两种后缀都认；保留原名是为了下次去上游对版本时文件名对得上。

## 有几个文件是自带门控的，这一点很要紧

它们自己在文件头里写着「输出不够大就整个不跑」，所以就算 C# 那边算出来的放大倍数和真实窗口不符，
链里也不会出现「一个放大器在缩小的画面上硬跑」这种事。**下面每一行都是从文件里的 `//!WHEN` 抄的，
不是从别处的说明转述的** —— 头一版这份表里有两条是错的（见最后一节），而一个差 0.2 的门控足以让某一档里的
放大器一次都不出手，屏上只是「好像没那么锐」。

- `ravu-lite-ar-*.hook`：`HOOKED.w OUTPUT.w / 0.707106 <`，也就是**输出不到 1.414 倍（√2）就不跑**。
  甜点档从 1.45 起，正好压在这个门槛上面一点 —— 所以这三个文件在甜点档和大倍数档全程出手，
  而整个微放大档（1.05–1.45）它们一次都不会跑。
- `ravu-zoom-ar-r2.hook`：`HOOKED.w OUTPUT.w <`，**只要在放大就跑**，而且直接输出到 OUTPUT 尺寸
  （这是清单里唯一能一步放到非整数倍的文件）。所以微放大档那一格是它。
- `ArtCNN_*.glsl`：`OUTPUT.w LUMA.w / 1.3 >`，**输出不到 1.3 倍就不跑**。所以「微放大档」
  （1.05–1.45 倍）里 1.05–1.30 那一段 ArtCNN 是不出手的，那一段由 `scale=ewa_lanczossharp` 收尾。
  1080p→1440p 是 1.33 倍，在门槛上面。
- `SSimDownscaler.glsl`：挂在 `POSTKERNEL` 上，`NATIVE_CROPPED.h POSTKERNEL.h >`，只在真的要缩小时跑。
  1.00 倍时它挂在链里一分钱不花 —— 这就是「原生」不必单开一档的原因。
- `SSimSuperRes.glsl`：同样挂 `POSTKERNEL`，`NATIVE_CROPPED.h OUTPUT.h <`，只在放大时跑。
- `igv/adaptive-sharpen.glsl`、`an3223/hdeband.glsl`、`an3223/nlmeans_light.glsl`：**没有门控，一律会跑**。
  所以它们只能靠档位表决定挂不挂 —— 前者只进中高档，后两个只进老片源那一半。

## CfL 和亮度放大器的先后关系（2026-09-03 实测）

用外部 `mpv.exe` 加一段 640×360 的 4:2:0 本地测试片、窗口 1280×720（正好 2.00 倍）、链
`ravu-lite-ar-r2 + CfL_Prediction_Lite`，用 IPC 读 `vo-passes` 量出来的。四条结论，档位表里每一格都靠它：

1. 亮度在 `RAVU-Lite-AR (step2)` 那一步变尺寸，也就是 2 倍发生在那里。
2. **CfL 的两个 pass 排在 ravu 两步之后**，所以它拿来回归色度的是**放大后**的亮度（1280×720），不是原始的
   640×360。
3. CfL 在这条链里能安全执行：每个 pass 的 `shaderc` 都报 0 errors，而开关 CfL 的两张截图确实不一样
   （红/绿硬边上的过渡像素从 4 个降到 3 个，洋红/青那条带明显变窄）。
4. **执行次序不跟列表走**（mpv 一律先跑完所有 LUMA 钩子再跑 CHROMA），**但列表次序决定 CfL 绑到哪一份亮度**：
   CfL 写在放大器后面时，色度直接输出到最终尺寸，mpv **一个色度缩放 pass 都不跑**（不挂 CfL 时那两个
   `ortho upscaling (spline36)` 就在）；CfL 写在前面时色度输出的是原始尺寸，再由 `cscale` 放大。
   所以「色度重建放最后」是一条真的指令，而 **`cscale` 在这套档位里是空转的** —— 任务书说的「以后单独试
   bilinear」没有可试的东西。

原始记录在 `artifacts/shader-probe/`（IPC 回包、mpv 日志、四张截图和一张放大对照图），那个目录在
`.gitignore` 里，不会进仓库。

## 出过箱的文件

**Anime4K Mode A（Fast）那六个，2026-09-04 出箱。** 上游 [bloc97/Anime4K](https://github.com/bloc97/Anime4K)
的 `glsl/Restore` 与 `glsl/Upscale`（入库时拍平成一层），许可 MIT。六个文件是
`Anime4K_Clamp_Highlights`、`Anime4K_Restore_CNN_M`、`Anime4K_Upscale_CNN_x2_M`、
`Anime4K_AutoDownscalePre_x2`、`Anime4K_AutoDownscalePre_x4`、`Anime4K_Upscale_CNN_x2_S`（这个次序就是
官方 Mode A 的次序，要装回来照这个写）。

出箱的原因是**没有一格再点它**：用户 2026-09-04 说「动画换 ArtCNN」，动画那三档（微放大、甜点、大倍数）
的低档全部换成 `ArtCNN_C4F16`，而这六个文件只在「动画 · 大倍数 · 低档」那一格用过。仓库里躺着没人点名的
着色器是单测判红的一种情形（白装几百 KB，还得跟着上游更新），所以按用户 2026-09-03 那句「删掉残留着色器
文件，有用再下载」一起删掉了。

**它们和 ArtCNN 的差别值得记一笔**（真要装回来的时候是这个理由）：Mode A 是这批文件里唯一为「分几步放大」
设计的链 —— 两个 `Upscale_CNN` 串起来，`AutoDownscalePre_x2/x4` 在够大了之后自己收尾，而且那两个
`Upscale_CNN` 本身没有下限门控（1.05 倍上也会出手）。ArtCNN 是一步 1.3 倍以上出手、一次放到两倍，超过
2.2 倍的那部分交给 `scale=ewa_lanczossharp`。另外它那两个 CNN pass 要求 `sigmoid-upscaling=no`（用户自己的
`mpv.conf` 里那一组就是这么写的），装回来时那条前置条件得跟着回来 —— 现在箱子里没有任何文件要求它，
有一条单测钉着这件事。

本机还留着一份备份（`artifacts/shader-attic/Anime4K/`，`artifacts/` 在 `.gitignore` 里，所以只在这台机器上）。

## 两个没有装箱、但被考虑过的文件

- `agyild` 的 `CAS.glsl`：门控是 `!(比例>1) && !(比例<1)`，也就是**只有输出面积和片源面积一模一样时才跑**。
  在这套档位里它一格都命中不了（缩小档在缩小，其余三档在放大），所以没有装箱 —— 和
  `FSRCNNX_x1`（根本不放大）是同一类错。要锐化就用 `igv/adaptive-sharpen.glsl`，它挂在 `OUTPUT` 上、没有门控。
- `igv/adaptive-sharpen_luma.glsl`：上游 gist 里没有这个文件（只有 `SCALED` 那一版），
  用户机器上那一份是第三方把 `//!HOOK SCALED` 改成 `//!HOOK LUMA` 得来的。不入库。

## 头一版这份说明里错了的两条

留着是因为「档位表照着这份说明写」，而这两条正好是会写坏一整格的那种错。都是拿文件里的 `//!WHEN`
逐条核对出来的：

- `ravu-lite` 的门控写成了 `0.833333`（1.2 倍），文件里是 `0.707106`，也就是 **1.414 倍**。差这 0.2 的后果是
  甜点档的下界（1.45）看着离门槛很远，其实只高出 0.036 —— 要是当初把甜点档定在 1.35，那一格的放大器一次
  都不会跑。
- `adaptive-sharpen.glsl` 写成挂在 `SCALED` 上，文件里是 `//!HOOK OUTPUT`。
