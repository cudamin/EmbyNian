using EmbyNian.Infrastructure;

namespace EmbyNian.Emby;

/// <summary>
/// 「下载到设备」把东西放在哪儿、叫什么名字。
/// <para>
/// 单独一份纯算术放在 Core，是为了钉住两件屏上不容易发现、又很难补救的事：<b>名字里不许有 Windows 不认的
/// 字符</b>（服务器上的剧名带着 <c>:</c> 或者 <c>?</c> 是常事，而 <see cref="System.IO.File"/> 只会抛一个
/// 「路径无效」，读起来像下载失败），以及<b>一部剧的一叠单集要落进它自己的文件夹</b>（否则二十四集平铺在
/// 「视频」目录里，和别的剧混在一起）。
/// </para>
/// <para>
/// 落点是写死的一个文件夹，不是每次弹窗问 —— 一次点下去就开始下，走到哪一步由提示条说，下完那一句话里带着
/// 完整路径。这件事是我定的，用户可以否：改成每次挑一个文件夹要接一个系统对话框，而那种对话框四道闸门验不到。
/// </para>
/// </summary>
public static class DownloadPlan
{
    /// <summary>系统的「视频」文件夹底下开的那一个。</summary>
    public const string FolderName = "EmbyNian";

    /// <summary>一个名字最多留多少个字。整条路径有上限，而剧名加集名本来就能写得很长。</summary>
    private const int MaxNameLength = 96;

    /// <summary>
    /// Windows 不许出现在文件名里的那几个。写死一份而不是问
    /// <see cref="System.IO.Path.GetInvalidFileNameChars"/>：那一份跟着操作系统走，于是同一个剧名在测试里和
    /// 在真机上会得出两个不同的文件名。
    /// </summary>
    private static readonly char[] Illegal = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>下载的根目录。</summary>
    public static string Root(string videos) => Path.Combine(videos, FolderName);

    /// <summary>
    /// 这台机器上的根目录：「视频」底下那一个。
    /// <para>
    /// 「视频」问不出来的时候（某些服务账号上是空串）退到用户目录 —— 空串配上
    /// <see cref="Path.Combine(string, string)"/> 得到的是一个相对路径，那会让影片落在程序自己的目录里，
    /// 而那个目录在发布件里可能根本写不了。
    /// </para>
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (videos.Length == 0) videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Root(videos.Length > 0 ? videos : Path.GetTempPath());
        }
    }

    /// <summary>
    /// 这一次下载落在哪个文件夹：一个文件就落在根上，一部剧或者一季各自开一个子文件夹。
    /// </summary>
    public static string Folder(string root, EmbyItem item) =>
        ItemMenu.IsEpisodeSet(item) ? Path.Combine(root, Safe(SetName(item))) : root;

    /// <summary>
    /// 一叠单集的文件夹叫什么。季用「剧名 第 1 季」而不是光一个「第 1 季」—— 后者在「视频」目录里谁都认不出
    /// 是哪部剧的。
    /// </summary>
    public static string SetName(EmbyItem item) =>
        item.Type == EmbyItemType.Season && item.SeriesName is { Length: > 0 } series
            ? $"{series} {item.Name}".Trim()
            : item.Name;

    /// <summary>
    /// 一个文件叫什么。名字用条目自己那一套说法（单集是「剧名 S01E02 集名」），后缀跟着服务器上那个文件走。
    /// <para>
    /// 用条目的名字而不是服务器上的文件名：那一头常是一长串发布组的记号（<c>Show.S01E02.1080p.WEB-DL.x265.mkv</c>），
    /// 而这一头正是客户端里到处都在用的说法。后缀非要跟着文件走不可 —— 一个 <c>.mkv</c> 存成 <c>.mp4</c>
    /// 播不出来。
    /// </para>
    /// </summary>
    public static string FileName(EmbyItem item, MediaSource? source)
    {
        var name = Safe(item.ToPlaybackTitle());
        if (name.Length == 0) name = Safe(item.Name);
        if (name.Length == 0) name = $"item-{Safe(item.Id)}";

        return name + Extension(source);
    }

    /// <summary>
    /// 后缀，带着那个点；问不出来就没有后缀 —— 猜一个错的比没有更糟。先问文件路径，再问容器：容器那一头
    /// 可能是一串（<c>mkv,mka</c>），头一个才是这个文件真正的样子。
    /// </summary>
    public static string Extension(MediaSource? source)
    {
        if (source?.Path is { Length: > 0 } path)
        {
            var dot = path.LastIndexOf('.');
            var slash = path.LastIndexOfAny(['/', '\\']);
            if (dot > slash && dot >= 0 && dot < path.Length - 1 && Suffix(path[(dot + 1)..]) is { } fromPath)
                return fromPath;
        }

        if (source?.Container is { Length: > 0 } container && Suffix(container.Split(',')[0].Trim()) is { } fromContainer)
            return fromContainer;

        return "";
    }

    /// <summary>
    /// 一段服务器上的文字当后缀用得上就返回带点的那一份，否则 null（也就是「没有后缀」）。
    /// <para>
    /// <b>「只认 ASCII 字母数字、最多八个」是这一段存在的全部理由，不是洁癖。</b>后缀是直接拼在文件名后头
    /// 的，而<see cref="MediaSource.Container"/>是服务器说什么就是什么 —— 文件名那一半有
    /// <see cref="Safe"/> 挡着分隔符，容器这一半从前一个字都没检查过，于是一个 <c>..\..\x</c> 就能把下载
    /// 的影片写到下载目录外面去（那一头 <c>File.Move(..., overwrite: true)</c> 还会盖掉同名文件）。带空格
    /// 或者冒号的值倒不会跑出去，只会让写文件抛一句「路径无效」，读起来像下载失败。
    /// </para>
    /// <para>
    /// 路径那一支本来就是这条规矩（长度加字母数字），所以两支现在共用一份 —— 从前只有一支有，而两支拼出来
    /// 的是同一个文件名。
    /// </para>
    /// </summary>
    private static string? Suffix(string value) =>
        value.Length is > 0 and <= 8 && value.All(char.IsAsciiLetterOrDigit)
            ? "." + value.ToLowerInvariant()
            : null;

    /// <summary>
    /// 一段服务器上的名字变成一个文件名能用的样子：不认的字符换成空格、连续空白并成一个、掐掉长度，末尾的点和
    /// 空格一起去掉（Windows 上那样的名字建不出来）。全是不认的字符时返回空串，由调用方决定退到什么。
    /// </summary>
    public static string Safe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var text = new string([.. name.Select(ch => Illegal.Contains(ch) || char.IsControl(ch) ? ' ' : ch)]);

        // 连续空白并成一个：换掉几个符号之后常出现「剧名   第一季」这样的空当。
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        text = string.Join(' ', parts);

        if (text.Length > MaxNameLength) text = text[..MaxNameLength].TrimEnd();

        return text.TrimEnd('.', ' ');
    }

    /// <summary>
    /// 要下一叠单集时，该拿哪两个 id 去问单集列表：<c>(剧 id, 季 id)</c>，剧 id 是空的就意味着问不出来、这一次
    /// 没有东西可下。
    /// <para>
    /// <b>剧用自己的 id，季用它所属剧的 id 加上自己这一季</b> —— 单集列表那个接口只认剧，把季的 id 当剧传进去
    /// 得到的是一个空列表，屏上写的是「服务器上找不到可下载的文件」，而那句话是假的。这一段从前写在外壳的
    /// 代码后置里，2026-09-05 搬进 Core 就为了这三行能被单测钉住。
    /// </para>
    /// <para>
    /// 「季却没有 SeriesId」这一档是真会出现的：卡片那一份来自列表接口，字段集是按页面要的那些取的。返回空的
    /// 剧 id 而不是拿季的 id 硬试，因为后者会以「这一季一集都没有」的样子回来。
    /// </para>
    /// </summary>
    public static (string? SeriesId, string? SeasonId) EpisodeQuery(EmbyItem item)
    {
        if (!ItemMenu.IsEpisodeSet(item)) return (null, null);

        var series = item.Type == EmbyItemType.Series ? item.Id : item.SeriesId;
        if (series is not { Length: > 0 }) return (null, null);

        return (series, item.Type == EmbyItemType.Season ? item.Id : null);
    }

    /// <summary>
    /// 提示条上那一行进度：「45%（1.2 GB / 2.7 GB）」，服务器没说总长度时只报已经下了多少。
    /// <para>
    /// 总长度是服务器给的，所以 0 和负数都要当成「没说」—— 拿它做除数会抛，而那一下抛在进度回调里，
    /// 表现是下载看着好好地卡住。
    /// </para>
    /// </summary>
    public static string Portion(long done, long? total) =>
        total is { } size and > 0
            ? $" {done * 100 / size}%（{TimeFormat.FileSize(done)} / {TimeFormat.FileSize(size)}）"
            : $" 已下载 {TimeFormat.FileSize(done)}";

    /// <summary>
    /// 下完之后提示条上最后那一句，连它是不是一句警告。
    /// <para>
    /// <b>这是用户唯一一次看得到完整落点的机会</b>，所以一个文件那一档报的是文件本身的路径，多个文件报的是
    /// 文件夹。四个分支全是用户看得见的文字，而在外壳里它们一条测试都没有 —— 2026-09-05 搬进 Core。
    /// </para>
    /// <para>
    /// 一个都没下成不是「成功了 0 个」，是一句警告：服务器上那几个条目都没有媒体源，也就是没有文件可下。
    /// 跳过的那几个要报出来，否则「已下载 19 个文件」在一部 24 集的剧上读起来像是下全了。
    /// </para>
    /// </summary>
    public static (string Text, bool Warning) Summary(
        string itemName,
        string folder,
        string lastPath,
        int files,
        int saved,
        int skipped) =>
        saved switch
        {
            0 => ($"「{itemName}」没有一个文件下得下来（服务器上都没有媒体源）", true),
            1 when files == 1 => ($"已下载到 {lastPath}", false),
            _ => ($"已下载 {saved} 个文件到 {folder}"
                + (skipped > 0 ? $"（{skipped} 个跳过：服务器上没有媒体源）" : ""), false)
        };
}
