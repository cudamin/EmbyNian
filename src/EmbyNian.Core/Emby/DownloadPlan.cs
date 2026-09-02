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
            if (dot > slash && dot >= 0 && dot < path.Length - 1)
            {
                var suffix = path[(dot + 1)..];
                if (suffix.Length <= 8 && suffix.All(char.IsLetterOrDigit))
                    return "." + suffix.ToLowerInvariant();
            }
        }

        if (source?.Container is { Length: > 0 } container)
        {
            var first = container.Split(',')[0].Trim();
            if (first.Length > 0) return "." + first.ToLowerInvariant();
        }

        return "";
    }

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
}
