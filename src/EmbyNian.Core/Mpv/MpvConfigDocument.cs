using System.Text;
using System.Text.RegularExpressions;

namespace EmbyNian.Mpv;

/// <summary>文档类型；两种文件共享无损行模型，但语法不同。</summary>
public enum MpvConfigKind
{
    Mpv,
    Input
}

/// <summary>一行 mpv 配置或按键绑定，Raw 在没有编辑时原样保留。</summary>
public enum MpvLineKind
{
    Blank,
    Comment,
    /// <summary><c>[name]</c> 配置组标题。</summary>
    Section,
    /// <summary>启用中的选项或按键绑定。</summary>
    Option,
    /// <summary>被 # 注释掉的选项或按键绑定。</summary>
    DisabledOption
}

/// <summary>
/// Parsed line kept verbatim.  Only a line changed through <see cref="SetValue"/> or
/// <see cref="Disable"/> is rebuilt, so whitespace, comments and unfamiliar options survive a save.
/// </summary>
public sealed class MpvConfigLine
{
    public required MpvLineKind Kind { get; init; }

    public required string Raw { get; set; }

    /// <summary>mpv option name, or the key token in input.conf.</summary>
    public string? Key { get; init; }

    /// <summary>mpv option value, or the command part of an input.conf binding.</summary>
    public string? Value { get; set; }

    /// <summary>从行尾注释符开始的文本（含 #）。</summary>
    public string? InlineComment { get; init; }

    public string? Section { get; init; }

    public string Indent { get; init; } = "";

    public bool IsEnabled => Kind == MpvLineKind.Option;

    public override string ToString() => Raw;
}

/// <summary>
/// 可无损编辑 mpv.conf 与 input.conf 的文档。未修改的文档序列化后保持逐字节一致。
/// </summary>
public sealed class MpvConfigDocument
{
    private static readonly Regex InputKey = new(
        "^(?:[A-Z][A-Z0-9_]*|F[0-9]{1,2}|(?:CTRL|ALT|SHIFT|META)\\+.+|KP_.+|MOUSE_.+|MBTN_.+|.)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly List<MpvConfigLine> _lines;

    private MpvConfigDocument(
        List<MpvConfigLine> lines,
        TextFileEncoding encoding,
        string newline,
        bool endsWithNewline,
        MpvConfigKind kind)
    {
        _lines = lines;
        Encoding = encoding;
        Newline = newline;
        EndsWithNewline = endsWithNewline;
        Kind = kind;
    }

    public IReadOnlyList<MpvConfigLine> Lines => _lines;

    public TextFileEncoding Encoding { get; }

    public string Newline { get; }

    public bool EndsWithNewline { get; }

    public MpvConfigKind Kind { get; }

    /// <summary>兼容旧调用，默认按 mpv.conf 语法解析。</summary>
    public static MpvConfigDocument Parse(string text, TextFileEncoding? encoding = null) =>
        Parse(text, MpvConfigKind.Mpv, encoding);

    public static MpvConfigDocument Parse(string text, MpvConfigKind kind, TextFileEncoding? encoding = null)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : text.Contains('\r') ? "\r" : "\n";
        var endsWithNewline = text.EndsWith('\n') || text.EndsWith('\r');
        var body = endsWithNewline ? text[..^newline.Length] : text;
        var rawLines = body.Length == 0 && !endsWithNewline ? [] : body.Split(newline);

        var lines = new List<MpvConfigLine>(rawLines.Length);
        var currentSection = (string?)null;
        foreach (var raw in rawLines)
            lines.Add(ParseLine(raw, kind, ref currentSection));

        return new MpvConfigDocument(lines, encoding ?? TextFileEncoding.Utf8NoBom, newline, endsWithNewline, kind);
    }

    public static MpvConfigDocument Load(string path) =>
        Load(path, InferKind(path));

    public static MpvConfigDocument Load(string path, MpvConfigKind kind)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = TextFileEncoding.Detect(bytes);
        return Parse(encoding.GetString(bytes), kind, encoding);
    }

    public string Serialize()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < _lines.Count; index++)
        {
            builder.Append(_lines[index].Raw);
            if (index < _lines.Count - 1) builder.Append(Newline);
        }

        if (EndsWithNewline) builder.Append(Newline);
        return builder.ToString();
    }

    public byte[] SerializeToBytes() => Encoding.GetBytes(Serialize());

    public IReadOnlyList<string> Sections =>
        [.. _lines.Where(line => line.Kind == MpvLineKind.Section).Select(line => line.Section!)];

    /// <summary>读取指定配置组中最后一次启用的值；null 表示全局段。</summary>
    public IEnumerable<MpvConfigLine> LinesIn(string? section)
    {
        var current = (string?)null;
        foreach (var line in _lines)
        {
            if (line.Kind == MpvLineKind.Section)
            {
                current = line.Section;
                if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) yield return line;
                continue;
            }

            if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) yield return line;
        }
    }

    public string? GetValue(string key, string? section = null) =>
        LinesIn(section)
            .Where(line => line.IsEnabled && string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Value)
            .LastOrDefault();

    public MpvConfigLine? FindLine(string key, string? section = null) =>
        LinesIn(section).LastOrDefault(line =>
            line.Kind is (MpvLineKind.Option or MpvLineKind.DisabledOption) &&
            string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sets an option or binding in place. A commented-out line is enabled rather than duplicated.
    /// For input.conf, <paramref name="key"/> is the key token and <paramref name="value"/> is the
    /// command string.
    /// </summary>
    public void SetValue(string key, string? value, string? section = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("配置项名称不能为空", nameof(key));

        var existing = FindLine(key, section);
        if (existing is not null)
        {
            Replace(existing, key, value);
            return;
        }

        var line = new MpvConfigLine
        {
            Kind = MpvLineKind.Option,
            Key = Kind == MpvConfigKind.Mpv ? key.Trim().ToLowerInvariant() : key.Trim(),
            Value = value,
            Indent = section is null ? "" : " ",
            Raw = ""
        };
        Rebuild(line);
        InsertAtEndOf(section, line);
    }

    public void Disable(string key, string? section = null)
    {
        var line = FindLine(key, section);
        if (line is null || line.Kind != MpvLineKind.Option) return;

        var replacement = new MpvConfigLine
        {
            Kind = MpvLineKind.DisabledOption,
            Key = line.Key,
            Value = line.Value,
            InlineComment = line.InlineComment,
            Indent = line.Indent,
            Raw = line.Indent + "#" + line.Raw.TrimStart()
        };
        _lines[_lines.IndexOf(line)] = replacement;
    }

    public void AppendRaw(string raw, MpvLineKind kind = MpvLineKind.Comment) =>
        _lines.Add(new MpvConfigLine { Kind = kind, Raw = raw });

    private static MpvConfigKind InferKind(string path) =>
        string.Equals(Path.GetFileName(path), "input.conf", StringComparison.OrdinalIgnoreCase)
            ? MpvConfigKind.Input
            : MpvConfigKind.Mpv;

    private static MpvConfigLine ParseLine(string raw, MpvConfigKind kind, ref string? currentSection)
    {
        var trimmed = raw.Trim();
        var indent = raw[..(raw.Length - raw.TrimStart().Length)];

        if (trimmed.Length == 0) return new MpvConfigLine { Kind = MpvLineKind.Blank, Raw = raw };

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
        {
            currentSection = trimmed[1..^1];
            return new MpvConfigLine { Kind = MpvLineKind.Section, Raw = raw, Section = currentSection, Indent = indent };
        }

        if (trimmed.StartsWith('#'))
        {
            var uncommented = trimmed.TrimStart('#').TrimStart();
            if (TrySplit(uncommented, kind, out var disabledKey, out var disabledValue, out var disabledComment))
            {
                return new MpvConfigLine
                {
                    Kind = MpvLineKind.DisabledOption,
                    Raw = raw,
                    Key = disabledKey,
                    Value = disabledValue,
                    InlineComment = disabledComment,
                    Indent = indent
                };
            }

            return new MpvConfigLine { Kind = MpvLineKind.Comment, Raw = raw, Indent = indent };
        }

        if (TrySplit(trimmed, kind, out var key, out var value, out var comment))
        {
            return new MpvConfigLine
            {
                Kind = MpvLineKind.Option,
                Raw = raw,
                Key = key,
                Value = value,
                InlineComment = comment,
                Indent = indent
            };
        }

        return new MpvConfigLine { Kind = MpvLineKind.Comment, Raw = raw, Indent = indent };
    }

    private static bool TrySplit(
        string text,
        MpvConfigKind kind,
        out string? key,
        out string? value,
        out string? comment)
    {
        key = null;
        value = null;
        comment = null;
        if (text.Length == 0) return false;

        var body = text;
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"') quoted = !quoted;
            else if (character == '#' && !quoted && (index == 0 || char.IsWhiteSpace(text[index - 1])))
            {
                comment = text[index..];
                body = text[..index];
                break;
            }
        }

        var trimmedBody = body.TrimEnd();
        if (trimmedBody.Length == 0) return false;

        if (kind == MpvConfigKind.Input)
        {
            var split = IndexOfWhitespace(trimmedBody);
            if (split <= 0) return false;
            var candidate = trimmedBody[..split].Trim();
            if (!InputKey.IsMatch(candidate)) return false;
            var command = trimmedBody[split..].Trim();
            if (command.Length == 0) return false;
            key = candidate;
            value = command;
            return true;
        }

        if (trimmedBody.StartsWith("--", StringComparison.Ordinal)) trimmedBody = trimmedBody[2..];

        var separator = trimmedBody.IndexOf('=');
        if (separator < 0)
        {
            if (!IsOptionName(trimmedBody)) return false;
            key = trimmedBody.ToLowerInvariant();
            return true;
        }

        key = trimmedBody[..separator].Trim().ToLowerInvariant();
        if (!IsOptionName(key))
        {
            key = null;
            return false;
        }

        value = trimmedBody[(separator + 1)..].Trim();
        return true;
    }

    private static int IndexOfWhitespace(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsWhiteSpace(value[index])) return index;
        return -1;
    }

    private static bool IsOptionName(string text) =>
        text.Length >= 2 &&
        char.IsAsciiLetter(text[0]) &&
        text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private void Replace(MpvConfigLine existing, string key, string? value)
    {
        var replacement = new MpvConfigLine
        {
            Kind = MpvLineKind.Option,
            Key = Kind == MpvConfigKind.Mpv ? key.Trim().ToLowerInvariant() : key.Trim(),
            Value = value,
            InlineComment = existing.InlineComment,
            Indent = existing.Indent,
            Raw = ""
        };
        Rebuild(replacement);
        _lines[_lines.IndexOf(existing)] = replacement;
    }

    private void Rebuild(MpvConfigLine line)
    {
        if (Kind == MpvConfigKind.Input)
        {
            var text = $"{line.Key} {line.Value}".TrimEnd();
            line.Raw = line.InlineComment is null
                ? line.Indent + text
                : $"{line.Indent}{text}  {line.InlineComment}";
            return;
        }

        var option = line.Value is null ? line.Key! : $"{line.Key}={line.Value}";
        line.Raw = line.InlineComment is null
            ? line.Indent + option
            : $"{line.Indent}{option}  {line.InlineComment}";
    }

    private void InsertAtEndOf(string? section, MpvConfigLine line)
    {
        if (section is null)
        {
            var firstSection = _lines.FindIndex(candidate => candidate.Kind == MpvLineKind.Section);
            var insertAt = firstSection < 0 ? _lines.Count : firstSection;
            while (insertAt > 0 && _lines[insertAt - 1].Kind == MpvLineKind.Blank) insertAt--;
            _lines.Insert(insertAt, line);
            return;
        }

        var header = _lines.FindIndex(candidate =>
            candidate.Kind == MpvLineKind.Section &&
            string.Equals(candidate.Section, section, StringComparison.OrdinalIgnoreCase));

        if (header < 0)
        {
            if (_lines.Count > 0) _lines.Add(new MpvConfigLine { Kind = MpvLineKind.Blank, Raw = "" });
            _lines.Add(new MpvConfigLine { Kind = MpvLineKind.Section, Section = section, Raw = $"[{section}]" });
            _lines.Add(line);
            return;
        }

        var end = header + 1;
        while (end < _lines.Count && _lines[end].Kind != MpvLineKind.Section) end++;
        while (end > header + 1 && _lines[end - 1].Kind == MpvLineKind.Blank) end--;
        _lines.Insert(end, line);
    }
}
