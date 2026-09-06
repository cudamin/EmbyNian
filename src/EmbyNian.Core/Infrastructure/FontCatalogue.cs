namespace EmbyNian.Infrastructure;

/// <summary>
/// One font family a person can pick, merged from every file that claims it — a family is usually
/// several files (regular, bold, italic) and sometimes several fonts inside one <c>.ttc</c>.
/// </summary>
public sealed class FontEntry
{
    private readonly string _haystack;

    internal FontEntry(string name, IEnumerable<string> alsoCalled)
    {
        Name = name;
        AlsoCalled = [.. alsoCalled];

        // The name in its own script, when the font states one: 微软雅黑 next to Microsoft YaHei. Shown
        // under the name rather than instead of it, because the one above is the one mpv is handed.
        Localized = AlsoCalled.FirstOrDefault(alias => alias.Any(character => character > 0x7F)) ?? string.Empty;

        // Folded once, at build time. Searching runs on every keystroke over every family on the
        // machine — several hundred of them — and folding case per comparison there is the difference
        // between a list that keeps up with typing and one that does not.
        _haystack = string.Join('\n', AlsoCalled.Prepend(Name)).ToLowerInvariant();
    }

    /// <summary>The family name: what the picker writes down and what mpv resolves.</summary>
    public string Name { get; }

    /// <summary>The same family's other names, searchable but not written down.</summary>
    public IReadOnlyList<string> AlsoCalled { get; }

    /// <summary>The first name in a non-Latin script, or empty when the font states none.</summary>
    public string Localized { get; }

    /// <summary>
    /// Whether every token is somewhere in this family's names. Every token rather than the whole
    /// string, so 「yahei light」 finds 「Microsoft YaHei Light」 and word order does not matter.
    /// </summary>
    public bool Matches(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        foreach (var token in tokens)
        {
            if (!_haystack.Contains(token, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>
    /// Whether this family is the one <paramref name="name"/> stands for — by its own name or by any
    /// alias. The picker asks this with the settings file's stored value, which may be either spelling:
    /// 方正中等线简体 and FZZhongDengXian-Z07S are one family, and a picker that showed the stored value
    /// only when it matched the primary name would sit unselected on exactly the setting it holds.
    /// </summary>
    public bool AnswersTo(string name) =>
        string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)
        || AlsoCalled.Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The font families on this machine, read from the font files themselves.
/// <para>
/// Immutable and built in one pass, so the scan can run off the UI thread and the finished catalogue
/// be handed over as a value. Search is a method here rather than in the view model because it is the
/// half of this feature with rules — token order, aliases, case folding — and Core is where a rule
/// can be tested.
/// </para>
/// </summary>
public sealed class FontCatalogue
{
    /// <summary>Nothing read yet. What the picker shows for the moment before the scan comes back.</summary>
    public static readonly FontCatalogue Empty = new([], 0);

    private static readonly string[] FontExtensions = [".ttf", ".ttc", ".otf", ".otc"];

    private static readonly char[] TokenBreaks = [' ', '\t', ',', '，', '、', ';', '；'];

    private FontCatalogue(IReadOnlyList<FontEntry> families, int files)
    {
        Families = families;
        FileCount = files;
    }

    /// <summary>Every family, by name, case-insensitively ordered.</summary>
    public IReadOnlyList<FontEntry> Families { get; }

    /// <summary>How many files the names came out of. Reported so a scan that read nothing says so.</summary>
    public int FileCount { get; }

    /// <summary>
    /// Where Windows keeps fonts: the machine-wide store, and the per-user one that 「install for me
    /// only」 writes to. The per-user directory does not exist on a machine nobody has installed a
    /// font on, which the scan treats as an empty directory rather than as a problem.
    /// <para>
    /// Where the program's own bundled fonts live is the caller's to add — the one real caller
    /// (<see cref="Services.FontLibrary"/>) appends the fonts folder next to the exe, which is the same
    /// directory playback hands mpv as <c>sub-fonts-dir</c>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Directories
    {
        get
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                user.Length == 0 ? string.Empty : Path.Combine(user, "Microsoft", "Windows", "Fonts")
            ];
        }
    }

    /// <summary>Every font file directly in each directory. Missing directories contribute nothing.</summary>
    public static FontCatalogue Scan(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        return FromFiles(directories.SelectMany(FontFiles));
    }

    /// <summary>The named files, whatever their extension. The unit tests' way in.</summary>
    public static FontCatalogue FromFiles(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var read = 0;

        foreach (var file in files)
        {
            var fonts = ReadNames(file);
            if (fonts.Count == 0) continue;

            read++;
            foreach (var font in fonts)
            {
                if (!merged.TryGetValue(font.Family, out var aliases))
                {
                    aliases = [];
                    merged.Add(font.Family, aliases);
                }

                foreach (var alias in font.AlsoCalled)
                {
                    if (!aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) aliases.Add(alias);
                }
            }
        }

        var families = merged
            .Select(pair => new FontEntry(pair.Key, pair.Value))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FontCatalogue(families, read);
    }

    /// <summary>
    /// The families whose names cover every word of the query; everything when the query is blank.
    /// </summary>
    public IReadOnlyList<FontEntry> Search(string? query)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0) return Families;

        return [.. Families.Where(entry => entry.Matches(tokens))];
    }

    /// <summary>
    /// The query as folded words. Public because the picker filters its own already-built rows with
    /// it — the same rules, without rebuilding a row object per keystroke.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? query) => query is null
        ? []
        : query.ToLowerInvariant().Split(TokenBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// This catalogue plus one name if it is not already in it. The settings file's font has to be in
    /// the list even when it is not installed: a picker that cannot show the current value looks like
    /// it has no value, and picking anything else would then be the only way out of it.
    /// <para>
    /// A name one of the families answers to as an alias does not get its own entry — 方正中等线简体
    /// stored in a settings file while the catalogue knows the same file as FZZhongDengXian-Z07S is one
    /// family, and a second row for it would be a ghost with no file behind the preview.
    /// </para>
    /// </summary>
    public FontCatalogue Including(string? name)
    {
        var wanted = (name ?? string.Empty).Trim();
        if (wanted.Length == 0) return this;
        if (Families.Any(entry => entry.AnswersTo(wanted))) return this;

        var families = Families
            .Append(new FontEntry(wanted, []))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FontCatalogue(families, FileCount);
    }

    private static IReadOnlyList<string> FontFiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return [];

        try
        {
            return
            [
                .. Directory.EnumerateFiles(directory)
                    .Where(file => FontExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            ];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<SfntFontNames> ReadNames(string path)
    {
        try
        {
            // ReadWrite sharing: the session has most of the installed fonts open already, and a font
            // opened exclusively would be a font the picker silently could not offer.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return SfntNames.Read(stream);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
