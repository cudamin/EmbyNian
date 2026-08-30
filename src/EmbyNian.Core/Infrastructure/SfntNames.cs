using System.Buffers.Binary;
using System.Text;

namespace EmbyNian.Infrastructure;

/// <summary>What one font inside a font file calls itself.</summary>
/// <param name="Family">The name Windows lists it under, and the one mpv is handed.</param>
/// <param name="AlsoCalled">
/// The same font's other names: its name in its own script (微软雅黑), and its typographic family
/// when that differs (「Yu Gothic」 for 「Yu Gothic Light」). Searching covers these too, so typing
/// 雅黑 finds a font whose name is spelled in Latin letters.
/// </param>
public sealed record SfntFontNames(string Family, IReadOnlyList<string> AlsoCalled);

/// <summary>
/// Reads family names out of a font file's <c>name</c> table.
/// <para>
/// Written out by hand because the alternatives are all off the table: Core takes no package
/// references and does not touch <c>System.Drawing</c>, and WinUI's own font enumeration lives in
/// the UI layer where nothing can unit-test it. A <c>name</c> table is not much of a format anyway —
/// an offset table, a table directory, and one string heap — and reading it ourselves means the scan
/// touches a few hundred bytes per file instead of loading fonts into the process.
/// </para>
/// <para>
/// Nothing here throws on a malformed file. A font directory is full of things a font parser did not
/// expect — half-copied files, files whose extension lies, and on some machines fonts with a name
/// table that no shipping product reads — and one bad file must cost that file only.
/// </para>
/// </summary>
public static class SfntNames
{
    private const uint TrueTypeOutlines = 0x00010000;
    private const uint OpenTypeOutlines = 0x4F54544F;  // 'OTTO'
    private const uint AppleTrueType = 0x74727565;     // 'true'
    private const uint Collection = 0x74746366;        // 'ttcf'
    private const uint NameTable = 0x6E616D65;         // 'name'

    private const int FamilyName = 1;
    private const int TypographicFamilyName = 16;
    private const ushort EnglishUnitedStates = 0x0409;

    /// <summary>A collection with more fonts than this is not one we are being handed honestly.</summary>
    private const uint MaxFontsPerFile = 512;

    /// <summary>Real name tables run a few kilobytes; the cap is only here so a bad length is not believed.</summary>
    private const uint MaxNameTableBytes = 1 << 20;

    private const int MaxNamesPerFont = 64;
    private const int MaxNameLength = 128;

    /// <summary>
    /// Every font in the file. Empty for anything that is not a font, and for a font that states no
    /// family name — a font no picker can offer, since there would be nothing to write down.
    /// </summary>
    public static IReadOnlyList<SfntFontNames> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var fonts = new List<SfntFontNames>();
        if (!stream.CanRead || !stream.CanSeek) return fonts;

        try
        {
            if (!Seek(stream, 0) || !TryReadUInt32(stream, out var kind)) return fonts;

            if (kind == Collection)
            {
                // ttcf: tag, version, font count, then one absolute offset per font. Read all the
                // offsets before following any of them — each font read seeks away from here.
                if (!TryReadUInt32(stream, out _) || !TryReadUInt32(stream, out var count)) return fonts;
                if (count is 0 or > MaxFontsPerFile) return fonts;

                var starts = new long[count];
                for (var index = 0; index < starts.Length; index++)
                {
                    if (!TryReadUInt32(stream, out var start)) return fonts;
                    starts[index] = start;
                }

                foreach (var start in starts)
                {
                    if (ReadFont(stream, start) is { } font) fonts.Add(font);
                }

                return fonts;
            }

            if (kind is TrueTypeOutlines or OpenTypeOutlines or AppleTrueType
                && ReadFont(stream, 0) is { } single)
            {
                fonts.Add(single);
            }
        }
        catch (IOException)
        {
            // A font that cannot be read all the way through contributes nothing and says nothing.
        }

        return fonts;
    }

    /// <summary>
    /// One font's names, given where its offset table starts — 0 for a plain font file, and the
    /// collection's entry for each font inside a <c>.ttc</c>.
    /// </summary>
    private static SfntFontNames? ReadFont(Stream stream, long start)
    {
        // Offset table: version (4), table count (2), then three fields we have no use for.
        if (!Seek(stream, start + 4) || !TryReadUInt16(stream, out var tables)) return null;
        if (tables == 0 || !Seek(stream, start + 12)) return null;

        var directory = new byte[tables * 16];
        if (!ReadFully(stream, directory)) return null;

        for (var index = 0; index < tables; index++)
        {
            var record = new ReadOnlySpan<byte>(directory, index * 16, 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(record) != NameTable) continue;

            var offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (length is < 6 or > MaxNameTableBytes || !Seek(stream, offset)) return null;

            var table = new byte[length];
            return ReadFully(stream, table) ? ParseNames(table) : null;
        }

        return null;
    }

    /// <summary>
    /// The <c>name</c> table: a header, one 12-byte record per string, and a heap the records point
    /// into. Only the two family-name IDs are of any interest; everything else in there is copyright
    /// text, style names and PostScript names.
    /// </summary>
    private static SfntFontNames? ParseNames(ReadOnlySpan<byte> table)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
        var heap = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
        if (count == 0 || 6 + (count * 12) > table.Length) return null;

        var families = new List<(bool English, string Text)>();
        var typographic = new List<(bool English, string Text)>();

        for (var index = 0; index < count; index++)
        {
            var record = table.Slice(6 + (index * 12), 12);
            var name = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            if (name is not (FamilyName or TypographicFamilyName)) continue;

            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
            var language = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            var offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);

            if (length == 0 || heap + offset + length > table.Length) continue;
            if (Decode(table.Slice(heap + offset, length), platform, encoding) is not { } text) continue;

            var bucket = name == FamilyName ? families : typographic;
            if (bucket.Count < MaxNamesPerFont) bucket.Add((IsEnglish(platform, language), text));
        }

        // nameID 1 in English first: that is the name Windows itself lists, the name a person types,
        // and the name mpv resolves. The typographic name is a fallback rather than a preference —
        // preferring it would merge 「Microsoft YaHei Light」 into 「Microsoft YaHei」 and lose the
        // distinction the picker exists to offer.
        var family = Preferred(families) ?? Preferred(typographic);
        if (family is null) return null;

        var aliases = families
            .Concat(typographic)
            .Select(entry => entry.Text)
            .Where(text => !string.Equals(text, family, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SfntFontNames(family, aliases);
    }

    /// <summary>The English spelling if the font states one, otherwise whatever it stated first.</summary>
    private static string? Preferred(List<(bool English, string Text)> names)
    {
        foreach (var entry in names)
        {
            if (entry.English) return entry.Text;
        }

        return names.Count > 0 ? names[0].Text : null;
    }

    /// <summary>
    /// The bytes as text, or null when this record is in an encoding whose strings are not names we
    /// can use. Windows (platform 3) and Unicode (platform 0) records are UTF-16 big endian;
    /// Macintosh (platform 1) records are a legacy byte encoding, taken only when every byte is
    /// ASCII, since a MacRoman table is not worth carrying to read a name the font also states in
    /// UTF-16.
    /// </summary>
    private static string? Decode(ReadOnlySpan<byte> bytes, ushort platform, ushort encoding)
    {
        string text;

        switch (platform)
        {
            case 3 or 0:
                if (bytes.Length % 2 != 0) return null;
                text = Encoding.BigEndianUnicode.GetString(bytes);
                break;

            case 1 when encoding == 0:
                foreach (var value in bytes)
                {
                    if (value is < 0x20 or > 0x7E) return null;
                }

                text = Encoding.ASCII.GetString(bytes);
                break;

            default:
                return null;
        }

        text = text.Trim();
        if (text.Length is 0 or > MaxNameLength) return null;

        foreach (var character in text)
        {
            if (char.IsControl(character) || character == '�') return null;
        }

        return text;
    }

    /// <summary>
    /// Whether this record is the English one. Windows records say so in the language ID; Macintosh
    /// records use 0 for English. Unicode (platform 0) records carry no language at all, so they are
    /// never the English one — they are only ever a fallback.
    /// </summary>
    private static bool IsEnglish(ushort platform, ushort language) => platform switch
    {
        3 => language == EnglishUnitedStates,
        1 => language == 0,
        _ => false
    };

    /// <summary>Moves to a position inside the file, false for one outside it.</summary>
    private static bool Seek(Stream stream, long position)
    {
        if (position < 0 || position > stream.Length) return false;

        stream.Position = position;
        return true;
    }

    /// <summary>
    /// Fills the buffer, false at end of file. <c>Stream.ReadExactly</c> would do this but throws,
    /// and a truncated font is an ordinary thing to find rather than an exceptional one.
    /// </summary>
    private static bool ReadFully(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer[read..]);
            if (got <= 0) return false;

            read += got;
        }

        return true;
    }

    private static bool TryReadUInt32(Stream stream, out uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        value = 0;
        if (!ReadFully(stream, buffer)) return false;

        value = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        return true;
    }

    private static bool TryReadUInt16(Stream stream, out ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        value = 0;
        if (!ReadFully(stream, buffer)) return false;

        value = BinaryPrimitives.ReadUInt16BigEndian(buffer);
        return true;
    }
}
