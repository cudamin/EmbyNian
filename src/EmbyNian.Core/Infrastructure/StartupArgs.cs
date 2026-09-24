namespace EmbyNian.Infrastructure;

/// <summary>
/// 命令行上那几个开关怎么读。外壳只收开关和两个带值的（<c>--screen</c>、<c>--theme</c>），所以是自己这一小段
/// 而不是一张选项表；读出来的那份结果是外壳那边的 <c>StartupOptions</c>。
/// <para>
/// 放在这里而不放在 <c>Program</c> 里，是因为验证这件事整个吊在它身上：截图、自检、逐页拍照、换主题拍一张，
/// 每一条命令都要先经过这三个函数。它读错一个字，看到的就是「开关没生效」—— 而那时人多半会去改被验的那一头。
/// 偏偏最容易读错的几种写法在屏上一次也碰不到：<c>--theme --dump-ui</c>（值没给，下一个词是另一个开关）、
/// <c>--screen=</c>（等号后面空着）、<c>/screen 2</c>（斜杠起头，Windows 上的老写法）。
/// </para>
/// </summary>
public static class StartupArgs
{
    private const string SelfCheckFlag = "--self-check";
    private const string SelfCheckDataFlag = "--self-check-data";

    /// <summary>
    /// 这个开关在不在。前面的横线和斜杠都不算数，大小写也不算 —— <c>--dump-ui</c>、<c>-dump-ui</c>、
    /// <c>/Dump-UI</c> 是同一个开关。
    /// </summary>
    public static bool Has(string[] args, string flag) =>
        args.Any(argument => string.Equals(
            argument.TrimStart('-', '/'), flag.TrimStart('-'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Self-check is a separate startup mode, including malformed requests. Validate before migration,
    /// probe dispatch or activation; unknown switches must never fall through to the normal client.
    /// </summary>
    public static bool RequestsSelfCheck(string[] args) => args.Any(argument =>
        argument.TrimStart('-', '/').Split('=')[0].Equals(SelfCheckFlag[2..], StringComparison.OrdinalIgnoreCase)
        || argument.TrimStart('-', '/').Split('=')[0].Equals(SelfCheckDataFlag[2..], StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the optional fresh data directory; throws for every unsupported combination.</summary>
    public static string? ValidateSelfCheck(string[] args)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? data = null;
        for (var index = 0; index < args.Length; index++)
        {
            var parts = args[index].TrimStart('-', '/').Split('=', 2);
            var name = parts[0].ToLowerInvariant();
            if (!seen.Add(name)) throw new ArgumentException("自检参数不能重复");
            if (name is "self-check" or "dump-ui")
            {
                if (parts.Length != 1) throw new ArgumentException("自检布尔开关不接受值");
                continue;
            }
            if (name is not ("self-check-data" or "screen" or "theme"))
                throw new ArgumentException("自检只允许 self-check、self-check-data、dump-ui、screen、theme");
            var value = parts.Length == 2 ? parts[1]
                : index + 1 < args.Length ? args[++index] : null;
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.StartsWith('/'))
                throw new ArgumentException("自检参数缺少有效的值");
            if (name == "screen" && (!int.TryParse(value, out var screen) || screen < 1))
                throw new ArgumentException("自检 screen 必须是从 1 开始的屏幕编号");
            if (name == "self-check-data") data = value;
        }
        if (!seen.Contains("self-check")) throw new ArgumentException("self-check-data 只能和 self-check 一起使用");
        return data;
    }

    /// <summary>
    /// 这个开关给的值，两种写法都认 —— <c>--theme misty</c> 或者 <c>--theme=misty</c>；开关没出现、或者后面
    /// 什么都没跟，就是 null。另起一个词但那个词自己也像开关的，不算值：<c>--theme --dump-ui</c> 读成「主题
    /// 开关没给值」，而不是一套叫 <c>--dump-ui</c> 的主题。
    /// </summary>
    public static string? Text(string[] args, string flag)
    {
        var name = flag.TrimStart('-');

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index].TrimStart('-', '/');

            if (argument.StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
                return Whole(argument[(name.Length + 1)..]);

            if (!string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) continue;

            if (index + 1 >= args.Length) return null;

            var next = args[index + 1];
            return next.StartsWith('-') || next.StartsWith('/') ? null : Whole(next);
        }

        return null;

        static string? Whole(string value) => value.Length == 0 ? null : value;
    }

    /// <summary>
    /// 这个开关给的数，两种写法都认 —— <c>--screen 2</c> 或者 <c>--screen=2</c>；开关没出现、或者后面跟的
    /// 不是个数，就是 null。不是数和没给这两件事在这里合成一件：两种情形下要的都是「照默认来」。
    /// </summary>
    public static int? Number(string[] args, string flag) =>
        int.TryParse(Text(args, flag), out var value) ? value : null;
}
