using EmbyMpvClient.Diagnostics;
using EmbyMpvClient.Infrastructure;

namespace EmbyMpvClient.Mpv;

/// <summary>
/// The client's own 「着色器配置组」, kept in a file of their own next to mpv.conf and pulled in
/// with <c>--include</c>.
/// <para>
/// They are deliberately not written into mpv.conf. That file is 111 KB of hand-tuned settings
/// maintained by an updater script; appending to it risks losing the user's work and would change
/// how a manually launched mpv behaves. A separate file is additive: nothing happens until this
/// client passes <c>--include</c> plus <c>--profile</c>, and deleting the file only costs the
/// automatic groups.
/// </para>
/// <para>
/// The groups target this machine — a Ryzen 5600G with Vega graphics driving a 1440p screen,
/// playing 1080p and 4K sources. Everything is sized for that: a 1080p source needs a 1.33×
/// upscale, which is cheap enough for a decent luma upscaler, while a 4K source is only ever
/// downscaled, so no upscaling shader in the chain would ever fire.
/// </para>
/// </summary>
public static class ShaderPack
{
    private const string Category = "shader";

    public const string FileName = "embympvclient.conf";

    /// <summary>Group applied to everything when 「所有视频」 is on.</summary>
    public const string DefaultProfileName = "2K-iGPU";

    /// <summary>Group applied when the item's metadata looks animated.</summary>
    public const string AnimeProfileName = "2K-iGPU-Anime";

    /// <summary>Group applied instead of the other two when the source is 4K.</summary>
    public const string HighResProfileName = "2K-iGPU-Light";

    /// <summary>Names this pack defines, in the order they appear in the file.</summary>
    public static IReadOnlyList<string> ProfileNames =>
        [DefaultProfileName, AnimeProfileName, HighResProfileName, "2K-iGPU-Anime+", "2K-iGPU-SD"];

    /// <summary>Where the pack belongs for a given mpv.conf location.</summary>
    public static string ResolvePath(string mpvConfigPath)
    {
        var directory = Path.GetDirectoryName(mpvConfigPath);
        return string.IsNullOrWhiteSpace(directory) ? FileName : Path.Combine(directory, FileName);
    }

    /// <summary>
    /// Writes the pack when it is not there yet. Never touches an existing file: once the user
    /// has tuned a group, an app update must not silently undo it.
    /// </summary>
    public static string? EnsureCreated(string mpvConfigPath)
    {
        var path = ResolvePath(mpvConfigPath);

        try
        {
            if (File.Exists(path)) return path;

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                Log.Warn(Category, $"mpv 配置目录不存在，无法写入着色器配置组：{directory}");
                return null;
            }

            Write(path);
            Log.Info(Category, $"已生成着色器配置组文件：{path}");
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn(Category, $"写入着色器配置组文件失败：{path}", error);
            return null;
        }
    }

    /// <summary>
    /// Overwrites the file with the shipped groups; used by the 「恢复默认」 button.
    /// <paramref name="shaderRoot"/> rewrites the <c>~~/shaders/</c> prefix of every entry to an
    /// absolute directory — the embedded backend uses this so mpv loads shader files from the
    /// program folder instead of the user's mpv config directory.
    /// </summary>
    public static void Write(string path, string? shaderRoot = null) =>
        AtomicFile.WriteAllText(path, BuildContent(shaderRoot), new System.Text.UTF8Encoding(false));

    /// <summary>The file's text. Public so a test can parse it without touching the disk.</summary>
    public static string BuildContent(string? shaderRoot = null)
    {
        var content = shaderRoot is null
            ? Content
            : Content.Replace("~~/shaders/", $"{shaderRoot.Replace('\\', '/')}/", StringComparison.Ordinal);
        return content.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    // mpv reads this file top to bottom when --include is processed; the groups only take effect
    // when something activates them, which the client does with --profile=<name>.
    private const string Content =
        """
        ##############################################################################
        # Emby MPV Client —— 着色器配置组
        #
        # 这个文件由 Emby MPV Client 生成，通过命令行 --include 载入，不会影响手动启动的 mpv。
        # 客户端会按下面的规则自动挑一个配置组，并用命令行 --profile= 覆盖 mpv.conf 里的全局
        # profile=（命令行在配置文件之后生效，所以不必改动 mpv.conf）。
        #
        #   针对硬件：Ryzen 5 5600G 核显（Vega）+ 2K（2560x1440）显示器
        #   针对片源：1080p（需要 1.33 倍放大）与 4K（只会缩小）
        #
        #   2K-iGPU         所有视频的默认组（实拍 1080p → 2K）
        #   2K-iGPU-Anime   元数据风格含「动画」时自动使用
        #   2K-iGPU-Light   片源高度 ≥ 1600 时使用：4K 在 2K 屏上只缩不放，放大着色器纯属浪费
        #   2K-iGPU-Anime+  更重的动画组，核显吃不吃得下取决于帧率，可在设置里手动选择
        #   2K-iGPU-SD      480p/720p 老片源（放大倍数大，用更强的放大器才划算）
        #
        # 想改成自己的口味：直接编辑本文件，或用客户端的「mpv 配置」页面。客户端只在文件不存在
        # 时生成一次，之后不会覆盖你的修改。
        ##############################################################################


        # ---------------------------------------------------------------------------
        # 默认组：1080p 实拍 → 2K
        #
        # ravu-zoom 直接按目标尺寸放大亮度平面，1.33 倍这种非整数比例正合适：先 2 倍再缩回来
        # 会白算一遍。r2 是给核显留的余量版本，画质差别很小而开销明显低于 r3。
        # SSimDownscaler 只在需要缩小时生效（4K 片源、或窗口小于视频时），带上不亏。
        # ---------------------------------------------------------------------------
        [2K-iGPU]
         profile-desc=2K 核显：1080p 实拍放大
         glsl-shaders="~~/shaders/ravu/ravu-zoom-ar-r2.glsl;~~/shaders/igv/SSimDownscaler.glsl"
         scale=ewa_lanczossharp             # ravu-zoom 已按目标尺寸输出，这里基本只是兜底
         cscale=spline36                    # 色度放大：比 bilinear 干净，代价可忽略
         dscale=mitchell                    # SSimDownscaler 要求的缩小算法
         linear-downscaling=no              # SSimDownscaler 必须关掉，否则会二次线性化
         correct-downscaling=yes
         sigmoid-upscaling=yes
         dither-depth=auto


        # ---------------------------------------------------------------------------
        # 动画组：Anime4K Mode A（Fast 档）
        #
        # 走的是官方 Mode A 的顺序：先修复线条（Restore CNN）再放大两次，中间用
        # AutoDownscalePre 防止在已经够大的时候继续放大。CNN 尺寸取 M/S 这一档，是核显上
        # 24~30fps 动画能稳住的组合；想要更细的线条用 2K-iGPU-Anime+。
        # Clamp_Highlights 放在最前面，抑制 CNN 在高光处的溢出。
        # ---------------------------------------------------------------------------
        [2K-iGPU-Anime]
         profile-desc=2K 核显：动画（Anime4K Fast）
         glsl-shaders="~~/shaders/Anime4K/glsl/Restore/Anime4K_Clamp_Highlights.glsl;~~/shaders/Anime4K/glsl/Restore/Anime4K_Restore_CNN_M.glsl;~~/shaders/Anime4K/glsl/Upscale/Anime4K_Upscale_CNN_x2_M.glsl;~~/shaders/Anime4K/glsl/Upscale/Anime4K_AutoDownscalePre_x2.glsl;~~/shaders/Anime4K/glsl/Upscale/Anime4K_AutoDownscalePre_x4.glsl;~~/shaders/Anime4K/glsl/Upscale/Anime4K_Upscale_CNN_x2_S.glsl;~~/shaders/igv/SSimDownscaler.glsl"
         scale=ewa_lanczossharp
         cscale=spline36
         dscale=mitchell
         linear-downscaling=no
         correct-downscaling=yes
         sigmoid-upscaling=no               # CNN 放大器自己处理线性/伽马，再叠一层会发灰
         deband=no                          # 动画的平滑渐变本就容易被去色带抹掉细节
         dither-depth=auto


        # ---------------------------------------------------------------------------
        # 4K 片源：只缩不放
        #
        # 4K 到 2K 全程是缩小，任何放大着色器都不会触发，挂着只是白占显存带宽。
        # SSimDownscaler 是这里唯一真正干活的，开销极低，缩出来比纯 mitchell 锐利得多。
        # ---------------------------------------------------------------------------
        [2K-iGPU-Light]
         profile-desc=2K 核显：4K 片源省电缩放
         glsl-shaders="~~/shaders/igv/SSimDownscaler.glsl"
         scale=bilinear                     # 不会用到：输出尺寸永远小于片源
         cscale=spline36
         dscale=mitchell
         linear-downscaling=no
         correct-downscaling=yes
         dither-depth=auto
         #hwdec=auto-safe                   # mpv.conf 里是 hwdec=no。4K HEVC 软解在 5600G 上
                                            # 能跑但风扇会响；想让核显接手就取消这行注释。


        # ---------------------------------------------------------------------------
        # 更重的动画组：ArtCNN C4F32
        #
        # 线条和网点比 Anime4K 干净一截，代价也高一截。核显上 24fps 通常没问题，60fps 的
        # OP/ED 或高帧率片源可能掉帧——掉帧就换回 2K-iGPU-Anime。
        # ---------------------------------------------------------------------------
        [2K-iGPU-Anime+]
         profile-desc=2K 核显：动画（ArtCNN，较吃性能）
         glsl-shaders="~~/shaders/Ani4K/Ani4Kv2_ArtCNN_C4F32_i2.glsl;~~/shaders/igv/SSimDownscaler.glsl"
         scale=ewa_lanczossharp
         cscale=spline36
         dscale=mitchell
         linear-downscaling=no
         correct-downscaling=yes
         deband=no
         dither-depth=auto


        # ---------------------------------------------------------------------------
        # 低清片源：480p/576p/720p
        #
        # 放大倍数到了 2~3 倍，这时候多花的算力才真的看得出来，用 FSRCNNX 的 distort 版本。
        # 客户端不会自动选它（自动规则只认动画和 4K），需要时在设置里手动指定。
        # ---------------------------------------------------------------------------
        [2K-iGPU-SD]
         profile-desc=2K 核显：低清片源增强
         glsl-shaders="~~/shaders/igv/FSRCNNX_x1_16-0-4-1_distort.glsl;~~/shaders/igv/SSimDownscaler.glsl"
         scale=ewa_lanczossharp
         cscale=spline36
         dscale=mitchell
         linear-downscaling=no
         correct-downscaling=yes
         sigmoid-upscaling=yes
         dither-depth=auto
        """;
}
