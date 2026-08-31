using System.Globalization;
using EmbyNian.Configuration;
using EmbyNian.Infrastructure;
using EmbyNian.Playback;

namespace EmbyNian.Emby;

/// <summary>One row of an audio or subtitle picker: a track, or one of the two 「没有明确选择」 answers.</summary>
/// <param name="Text">What the row reads. The only thing the picker shows.</param>
/// <param name="Stream">The track to select, or null for 自动 and for 不使用字幕.</param>
/// <param name="Disable">
/// True only for 不使用字幕. Distinguishes it from 自动: both carry no stream, but one means 「let the
/// client's own resolution stand」 and the other means 「pass --sid=no」.
/// </param>
public sealed record TrackRow(string Text, MediaStream? Stream, bool Disable);

/// <summary>
/// One row of the 媒体源 picker: a file, and what the picker calls it.
/// <para>
/// Paired rather than displayed through a converter because the picker's selection has to hand back the
/// <see cref="MediaSource"/> itself — pressing 播放 needs the file, not its label. This lived as a private
/// record inside the detail page, which meant the page could not bind <c>DisplayMemberPath</c> in markup
/// (an inaccessible type has no bindable member) and the rows could not be tested; the picker's index
/// arithmetic in <see cref="ItemDetail.PickSource"/> is exactly the part worth a test.
/// </para>
/// </summary>
public sealed record SourceRow(MediaSource Source, string Text);

/// <summary>One credited person, already shaped into the card a shelf can draw.</summary>
public sealed record CastCredit(EmbyItem Card, string Credit);

/// <summary>
/// One line of the 媒体信息 table: a right-justified label and one or two values.
/// <para>
/// Two values rather than one because a file's subtitle list runs to a dozen entries, and a single
/// column of them pushes everything below it off the screen — the table pairs those across its two
/// value columns, 01/02 on one line and 03/04 on the next. A row that fills only <c>Value</c> spans
/// both columns instead, so a long file name is never squeezed into half the width.
/// </para>
/// <para>
/// <see cref="Span"/> is that rule as a number the markup binds straight to <c>Grid.ColumnSpan</c>.
/// The alternative is a converter in the view, and this way the same arithmetic the tests check is
/// the arithmetic the page lays out with.
/// </para>
/// </summary>
/// <param name="Label">
/// The label, already spaced and terminated — 「文 件：」. Empty on a continuation line, which is how a
/// list of tracks reads as one labelled block rather than as one label per track.
/// </param>
public sealed record InfoRow(string Label, string Value, string Second = "")
{
    /// <summary>How many of the table's two value columns <see cref="Value"/> fills.</summary>
    public int Span => Second.Length == 0 ? 2 : 1;
}

/// <summary>
/// Every string and list the 详情页 puts on screen, as pure functions of an already-fetched item.
/// <para>
/// Split out of the page rather than left in it for two reasons. The first is that the test project
/// references only this assembly — a facts line built inside a WinForms or WinUI view is a facts line
/// no test can assert, which is exactly how the WinForms version's dozen display helpers ended up
/// with none. The second is the page's own layout rule: visibility is assigned in one place and never
/// read back, so 「this row has nothing to say」 has to be expressible as a value. Every function here
/// returns an empty string or an empty list for that case, and the page hides the row on the emptiness
/// rather than on a flag it has to keep in step.
/// </para>
/// <para>
/// Nothing here touches the network, the clock or the culture of the machine: dates are formatted with
/// <see cref="CultureInfo.InvariantCulture"/> so the same item reads the same on any host.
/// </para>
/// </summary>
public static class ItemDetail
{
    /// <summary>How many portraits the cast shelf will draw before it stops.</summary>
    public const int CastLimit = 40;

    /// <summary>How many cards the 更多类似 row holds. Long enough to fill the strip, short enough for one round trip.</summary>
    public const int SimilarLimit = 12;

    /// <summary>
    /// 导演, the prose line under 简介 — Emby's own detail page lists them there rather than leaving
    /// them buried in the cast shelf. Empty for an item with no director credited, which hides the line
    /// rather than showing 「导演: 」 with nothing after it.
    /// </summary>
    public static string Directors(EmbyItem item)
    {
        var names = item.People
            .Where(person => person.Type == "Director" && person.Name.Length > 0)
            .Select(person => person.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names.Count == 0 ? "" : $"导演：{string.Join("、", names)}";
    }

    /// <summary>
    /// The headline. An episode is billed under its show's name, because the show is where the reader
    /// thinks they are; the episode's own name goes on the line below.
    /// </summary>
    public static string Title(EmbyItem item) =>
        item.Type == EmbyItemType.Episode && item.SeriesName is { Length: > 0 } series ? series : item.Name;

    /// <summary>
    /// Where the headline leads, or null when it leads nowhere. An episode is billed under its show's
    /// name (see <see cref="Title"/>), and pressing that name is asking for the thing it names: the
    /// season, which is the page that lists this episode among its siblings.
    /// <para>
    /// A stub item rather than the parent itself — the same shape <see cref="Cast"/> builds. An episode
    /// carries only its parents' ids and names, and the detail page re-fetches whatever it is handed by
    /// id, so id, name and type are the whole of what a navigation needs.
    /// </para>
    /// <para>
    /// The season is preferred over the show because that is the narrower answer to 「where is this
    /// episode」; a server that left the season id out falls back to the show, and an episode with
    /// neither has no link at all rather than a button that goes nowhere.
    /// </para>
    /// </summary>
    public static EmbyItem? TitleTarget(EmbyItem item)
    {
        if (item.Type != EmbyItemType.Episode) return null;

        if (item.SeasonId is { Length: > 0 } seasonId)
            return new EmbyItem
            {
                Id = seasonId,
                Name = item.SeasonName ?? item.SeriesName ?? item.Name,
                Type = EmbyItemType.Season
            };

        if (item.SeriesId is { Length: > 0 } seriesId)
            return new EmbyItem
            {
                Id = seriesId,
                Name = item.SeriesName ?? item.Name,
                Type = EmbyItemType.Series
            };

        return null;
    }

    /// <summary>
    /// The line under the headline: the episode on an episode page, otherwise the genres.
    /// </summary>
    public static string Subline(EmbyItem item)
    {
        if (item.Type == EmbyItemType.Episode) return EpisodeLabel(item);

        if (item.Genres.Count > 0) return string.Join("  ·  ", SublineGenres(item));

        return item.Type == EmbyItemType.Season && item.SeriesName is { Length: > 0 } series ? series : "";
    }

    /// <summary>
    /// 副标题那一行里点得动的那几个类型 —— 详情页上那一行现在一个类型是一个入口（见
    /// <c>IShellActions.OpenGenre</c>）。
    /// <para>
    /// 空表示这一行不是类型列表：单集页上它是「S2:E7 - 集名」，没有类型的季页上是剧名 —— 那两种照旧画一行
    /// 普通的字。<see cref="Subline"/> 拿的是同一份，所以「最多几个」和「哪几个」两处不会分叉。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SublineGenres(EmbyItem item) =>
        item.Type == EmbyItemType.Episode ? [] : [.. item.Genres.Take(SublineGenreLimit)];

    /// <summary>
    /// 那一行最多列几个类型。六个是版面给的：再多就要折行，而它上面就是整页最大的那行片名。
    /// </summary>
    private const int SublineGenreLimit = 6;

    /// <summary>
    /// 「S1:E2 - 旅途的终点」 — an episode named the way it is read in prose, wherever it is named: under
    /// its own headline, and on the 下一集 line of the show it belongs to.
    /// <para>
    /// The code here is deliberately not <see cref="EmbyItem.EpisodeCode"/>. That one is zero padded
    /// (S01E02) because it is used where it has to sort and line up; this one sits next to the episode's
    /// title, and 「S1:E2」 is how the reference writes it.
    /// </para>
    /// </summary>
    public static string EpisodeLabel(EmbyItem item)
    {
        var code = item.ParentIndexNumber is { } season && item.IndexNumber is { } number
            ? $"S{season}:E{number}"
            : "";

        return code.Length > 0 ? $"{code} - {item.Name}" : item.Name;
    }

    /// <summary>
    /// The score for the badge, or empty when the server has none. Formatted 「8.4」 / 「8」 rather than
    /// 「8.40」, and invariantly, so a machine whose decimal separator is a comma still shows a dot.
    /// </summary>
    public static string Score(EmbyItem item) =>
        item.CommunityRating is { } score and > 0 ? score.ToString("0.#", CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// The facts line's first segment: 「2016 – 2018」 for a show, 「2016/4/17」 for everything else.
    /// <para>
    /// A show is dated in years because that is the fact about a show — 「2016/4/17」 is the day one
    /// episode aired, and printing it beside a title that spans three years states something narrower
    /// than the truth. The range closes only when the server sent an <c>EndDate</c> in a later year: a
    /// show still on the air has no second year to print, and one that ran inside a single year would
    /// otherwise read 「2018 – 2018」.
    /// </para>
    /// </summary>
    public static string Years(EmbyItem item)
    {
        if (item.Type == EmbyItemType.Series)
        {
            var start = item.ProductionYear is { } production and > 0
                ? production
                : item.PremiereDate?.ToLocalTime().Year;

            if (start is not { } from) return "";

            return item.EndDate?.ToLocalTime().Year is { } until && until > from
                ? $"{from.ToString(CultureInfo.InvariantCulture)} – {until.ToString(CultureInfo.InvariantCulture)}"
                : from.ToString(CultureInfo.InvariantCulture);
        }

        // The premiere date beats the year because it is the same fact told more precisely; a server
        // that only knows the year still gets a segment rather than a gap.
        if (item.PremiereDate is { } premiere)
            return premiere.ToLocalTime().ToString("yyyy/M/d", CultureInfo.InvariantCulture);

        return item.ProductionYear is { } year and > 0 ? year.ToString(CultureInfo.InvariantCulture) : "";
    }

    /// <summary>
    /// Who made it: 「TBS」. The first credited company only — Emby lists every one of them, and four
    /// studio names in the facts line push the runtime and the rating off the end of it. Left off an
    /// episode, whose studio is its show's and is already stated on the show's own page.
    /// </summary>
    public static string Studio(EmbyItem item)
    {
        if (item.Type == EmbyItemType.Episode) return "";

        return item.Studios.FirstOrDefault(studio => !string.IsNullOrWhiteSpace(studio.Name))?.Name.Trim() ?? "";
    }

    /// <summary>The dim segments right of the score badge: years, studio, runtime, rating, season count.</summary>
    public static IEnumerable<string> FactParts(EmbyItem item)
    {
        if (Years(item) is { Length: > 0 } years) yield return years;
        if (Studio(item) is { Length: > 0 } studio) yield return studio;
        if (item.RunTimeTicks is > 0) yield return TimeFormat.Duration(item.RunTimeTicks);
        if (item.OfficialRating is { Length: > 0 } rating) yield return rating;
        if (item.Type == EmbyItemType.Series && item.ChildCount is { } seasons and > 0) yield return $"共 {seasons} 季";
    }

    /// <summary>The same segments as one string; empty when the server knew none of them.</summary>
    public static string Facts(EmbyItem item) => string.Join("  ·  ", FactParts(item));

    /// <summary>
    /// 「视频：  1080p · HEVC · MKV · 8.4 GB」, or empty when the item carries no media source — which is
    /// every series and season page, since a show is not a file.
    /// </summary>
    public static string VideoLine(MediaSource? source)
    {
        if (source is null) return "";
        var quality = source.ToQualityLabel();
        return quality.Length > 0 ? $"视频：  {quality}" : "";
    }

    /// <summary>
    /// What the play button reads. The resume position goes on the button itself rather than behind a
    /// switch, which is what makes 从头开始 a plain second button instead of a toggle.
    /// </summary>
    public static string PlayText(EmbyItem? target) => target switch
    {
        null => "播放",
        { HasResumePosition: true } => $"继续播放 {TimeFormat.Clock(target.ResumeTicks)}",
        _ when target.Type == EmbyItemType.Episode && target.EpisodeCode.Length > 0 => $"播放 {target.EpisodeCode}",
        _ => "播放"
    };

    /// <summary>
    /// The 下一集 line on a show's page: 「S2:E7 - 逮捕才干的律师」, the episode 播放 would start.
    /// <para>
    /// Empty on an episode's own page, where the same string is already the second line of the headline,
    /// and empty for a film, whose next thing to watch is itself. Built from what this client resolved
    /// (<see cref="PickSeason"/> then <see cref="PickEpisode"/>) rather than from the server's
    /// <c>Shows/NextUp</c>: two answers to 「what plays next」 that can disagree is one too many, and the
    /// button beside this line obeys the resolved one.
    /// </para>
    /// </summary>
    public static string NextUpTitle(EmbyItem page, EmbyItem? target)
    {
        if (page.Type == EmbyItemType.Episode) return "";
        if (target is null || target.Type != EmbyItemType.Episode) return "";

        return EpisodeLabel(target);
    }

    /// <summary>
    /// 「剩余 44 分钟」 — what is left of the episode from where it was paused. Empty unless there is a
    /// pause to be left of: an untouched episode's remainder is its runtime, which the facts line above
    /// already states, and repeating it as 「剩余」 would read like progress nobody made.
    /// </summary>
    public static string NextUpRemaining(EmbyItem? target)
    {
        if (target is not { HasResumePosition: true }) return "";
        if (target.RunTimeTicks is not { } total || total <= target.ResumeTicks) return "";

        return $"剩余 {TimeFormat.Duration(total - target.ResumeTicks)}";
    }

    /// <summary>
    /// How far into the next episode the bar sits, 0..100 for a <c>ProgressBar</c>'s own scale. Zero
    /// when nothing was watched, which the page reads as 「no bar to draw」.
    /// </summary>
    public static double NextUpProgress(EmbyItem? target) =>
        target is { HasResumePosition: true } ? target.ProgressFraction * 100 : 0;

    /// <summary>
    /// A media source as the picker lists it. The name is what Emby calls the file, which for a
    /// multi-version film is 「4K HDR」 or 「导演剪辑版」 and is the whole reason the picker exists; the
    /// filename is the fallback, because a source with no name is still a distinguishable file.
    /// </summary>
    public static string SourceLabel(MediaSource source)
    {
        var quality = source.ToQualityLabel();
        var name = string.IsNullOrWhiteSpace(source.Name)
            ? System.IO.Path.GetFileName(source.Path ?? "")
            : source.Name!;

        if (string.IsNullOrWhiteSpace(name)) return quality.Length > 0 ? quality : "默认媒体源";
        return quality.Length > 0 ? $"{name}  ·  {quality}" : name;
    }

    /// <summary>The 媒体源 picker's rows, in the order the server returned the files.</summary>
    public static IReadOnlyList<SourceRow> SourceRows(EmbyItem item) =>
        item.MediaSources.Select(source => new SourceRow(source, SourceLabel(source))).ToList();

    /// <summary>
    /// Which row the picker should start on: the one holding <paramref name="wanted"/>, or none.
    /// <para>
    /// Matched by reference first and by id second, because the two are not the same question. The
    /// item's <c>DefaultMediaSource</c> is normally one of the very objects in this list, and reference
    /// equality settles that without trusting ids at all — Emby returns an empty source id for some
    /// direct-play files, and three empty ids all「match」each other. The id comparison is the fallback
    /// for a source that came from a second fetch of the same item and is therefore a different object.
    /// </para>
    /// </summary>
    /// <returns>The row, or null when there is nothing to select.</returns>
    public static SourceRow? PickSource(IReadOnlyList<SourceRow> rows, MediaSource? wanted)
    {
        if (wanted is null || rows.Count == 0) return null;

        return rows.FirstOrDefault(row => ReferenceEquals(row.Source, wanted))
            ?? (string.IsNullOrEmpty(wanted.Id)
                ? null
                : rows.FirstOrDefault(row => string.Equals(row.Source.Id, wanted.Id, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The audio picker's rows, 自动 first.
    /// <para>
    /// 自动 is named rather than left blank because this client, not mpv, resolves it: the settings
    /// page's 音轨语言 is applied against this very file, so the first row can say which track pressing
    /// 播放 would actually start with instead of shrugging.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TrackRow> AudioRows(PlaybackSettings settings, MediaSource source)
    {
        var auto = TrackSelection.Resolve(settings, source);

        var rows = new List<TrackRow> { new(AutoLabel(auto.Audio?.ToDisplayLabel()), null, false) };
        rows.AddRange(source.AudioStreams.Select(stream => new TrackRow(stream.ToDisplayLabel(), stream, false)));
        return rows;
    }

    /// <summary>
    /// The subtitle picker's rows: 自动, then the explicit 不使用字幕, then the file's own tracks.
    /// <para>
    /// 不使用字幕 exists as its own row because 自动 can legitimately resolve to no subtitles (the
    /// 字幕模式 is Off, or nothing matched), and a user who wants that permanently for this file needs
    /// to be able to say so rather than hope the automatic answer stays put.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TrackRow> SubtitleRows(PlaybackSettings settings, MediaSource source)
    {
        var auto = TrackSelection.Resolve(settings, source);

        var automatic = auto.Subtitle.Disabled
            ? "自动（不显示字幕）"
            : AutoLabel(auto.Subtitle.Stream?.ToDisplayLabel());

        var rows = new List<TrackRow>
        {
            new(automatic, null, false),
            new("不使用字幕", null, true)
        };

        rows.AddRange(source.SubtitleStreams.Select(stream => new TrackRow(stream.ToDisplayLabel(), stream, false)));
        return rows;
    }

    /// <summary>The 自动 row's text: what the settings would pick, or who decides when nothing does.</summary>
    public static string AutoLabel(string? picked) =>
        string.IsNullOrWhiteSpace(picked) ? "自动（由 mpv 决定）" : $"自动 · {picked}";

    /// <summary>
    /// 媒体信息 as a label/value table: the file this page would play, spelled out the way MediaInfo
    /// spells it — release name, codec, size, running time, resolution, bit rate, HDR flavour, bit depth,
    /// frame rate, then every audio and subtitle track numbered.
    /// <para>
    /// A table rather than the paragraph of prose this used to be because the numbers are what the reader
    /// came here for: with a label column they are found by scanning down one edge, and 「码 率」 and
    /// 「色 深」 read as answers rather than as a sentence. Empty for nothing worth saying, as everywhere
    /// else in this class.
    /// </para>
    /// <para>
    /// The 着色器 decision used to be the last line and is deliberately gone. Resolving it needs the
    /// genres of the parent show — an episode carries none of its own — and this panel is now only ever
    /// shown on the page of the file itself, where no parent is in hand; naming a group here that
    /// playback then did not apply is worse than not naming one. 播放信息 during playback reports the
    /// group that actually ran, with the settings and the parent both available.
    /// </para>
    /// </summary>
    /// <param name="source">
    /// The source the table describes — the one the 媒体源 picker is showing, not necessarily the item's
    /// default. A film held twice at two resolutions has two release names and two bit rates, and a table
    /// that always described the first would contradict the picker directly above it.
    /// </param>
    public static IReadOnlyList<InfoRow> MediaInfo(EmbyItem item, MediaSource? source)
    {
        if (source is null) return [new InfoRow("媒体源：", "服务器没有返回可播放的媒体源。")];

        var video = source.PrimaryVideoStream;
        var rows = new List<InfoRow>(14);

        // Labels carry their own spacing and their own colon: 「文 件：」 sits between two-character and
        // three-character labels in the same right-aligned column, and the space is what makes them read
        // as one width. A row with nothing to say is not added at all rather than added empty.
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new InfoRow($"{label}：", value!));
        }

        Add("文 件", FileName(source));
        Add("格 式", Segments(Upper(video?.Codec), video?.Profile, Upper(source.Container)));
        Add("大 小", TimeFormat.FileSize(source.Size));
        Add("时 长", TimeFormat.Duration(source.RunTimeTicks));
        Add("分辨率", video is { Width: > 0, Height: > 0 } ? $"{video.Width} x {video.Height}" : null);
        Add("码 率", Bitrate(video?.BitRate ?? source.Bitrate));
        Add("HDR", video is { IsHdr: true } ? video.VideoRange : null);
        Add("色 深", video?.BitDepth is > 0 ? $"{video.BitDepth} bits" : null);
        Add("帧 率", video is { FrameRate: > 0 } ? Fps(video.FrameRate) : null);

        // Tracks are numbered from 01, and the number belongs to the value rather than to the label: the
        // reader is matching these against the 音轨 and 字幕 pickers above, which list them in this same
        // order. Audio takes a line each because its labels run long; subtitles pair up across the two
        // value columns, so a file with ten of them costs five lines instead of ten.
        var audio = source.AudioStreams.ToList();
        for (var i = 0; i < audio.Count; i++) rows.Add(new InfoRow(i == 0 ? "音 频：" : "", Track(i, audio[i])));

        var subtitles = source.SubtitleStreams.ToList();
        for (var i = 0; i < subtitles.Count; i += 2)
        {
            var second = i + 1 < subtitles.Count ? Track(i + 1, subtitles[i + 1]) : "";
            rows.Add(new InfoRow(i == 0 ? "字 幕：" : "", Track(i, subtitles[i]), second));
        }

        // Ours, not MediaInfo's: which disk and which library the file came from is the question a user of
        // this app actually asks of a path, and the file name above already answers the other half.
        Add("路 径", Folder(source));
        Add("标 签", item.Tags.Count == 0 ? null : string.Join("、", item.Tags.Take(8)));

        return rows;
    }

    /// <summary>
    /// The release name: the file with neither its directory nor its extension, because 路 径 and 格 式
    /// spell out both on their own lines. Falls back to the name the 媒体源 picker shows for a source the
    /// server described without a path.
    /// </summary>
    private static string FileName(MediaSource source) =>
        source.Path is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : source.Name ?? "";

    /// <summary>The directory the file sits in — which share, which library — without repeating the name.</summary>
    private static string Folder(MediaSource source) =>
        source.Path is { Length: > 0 } path ? Path.GetDirectoryName(path) ?? "" : "";

    /// <summary>The parts of a compound value that the server actually sent, joined the way labels are.</summary>
    private static string Segments(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string? Upper(string? value) => value?.ToUpperInvariant();

    /// <summary>
    /// 「24.2 Mb/s」 for a video stream, 「768 kb/s」 for a track. Decimal, not binary: bit rates are
    /// quoted in powers of ten wherever they are quoted, unlike the file sizes right above them.
    /// </summary>
    private static string Bitrate(int? bits) => bits switch
    {
        null or <= 0 => "",
        < 1_000_000 => $"{(bits.Value / 1000d).ToString("0.#", CultureInfo.InvariantCulture)} kb/s",
        _ => $"{(bits.Value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)} Mb/s"
    };

    /// <summary>
    /// 「23.976 FPS」. Emby forwards only the decimal, so the exact ratio a release carries
    /// (24000/1001) is not ours to print — reconstructing it from 23.976 would be a guess.
    /// </summary>
    private static string Fps(double rate) => $"{rate.ToString("0.###", CultureInfo.InvariantCulture)} FPS";

    /// <summary>
    /// 「01: English DDP 5.1 Atmos @768 kb/s」. Numbered from one and zero-padded so ten of them read down
    /// a straight edge, and the rate appended only when the server sent one for that track.
    /// </summary>
    private static string Track(int index, MediaStream stream)
    {
        var rate = Bitrate(stream.BitRate);
        var number = (index + 1).ToString("00", CultureInfo.InvariantCulture);
        return $"{number}: {stream.ToDisplayLabel()}" + (rate.Length == 0 ? "" : $" @{rate}");
    }

    /// <summary>
    /// The credited people, deduplicated and capped, each already wrapped in the item a poster shelf
    /// can draw.
    /// <para>
    /// Emby lists someone once per credit, so a director who also wrote the episode arrives twice. The
    /// first credit is the billed one and a second card for the same face is noise, so the first wins
    /// and the duplicate is dropped rather than merged — 「导演、编剧」 under one portrait would be a
    /// third thing to format for a case nobody reads closely.
    /// </para>
    /// <para>
    /// The card is typed <see cref="EmbyItemType.Person"/>, which is not playable, so no shelf grows a
    /// play badge or a 已看 tick on a face. The portrait tag is copied into <c>ImageTags</c> because
    /// that is where the image store looks, so fetching a photo needs no new code path.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CastCredit> Cast(EmbyItem item, int limit = CastLimit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cast = new List<CastCredit>();

        foreach (var person in item.People)
        {
            if (person.Id.Length == 0 || person.Name.Length == 0) continue;
            if (!seen.Add(person.Id)) continue;

            var card = new EmbyItem { Id = person.Id, Name = person.Name, Type = EmbyItemType.Person };
            if (person.PrimaryImageTag is { Length: > 0 } tag) card.ImageTags["Primary"] = tag;

            cast.Add(new CastCredit(card, person.Credit));
            if (cast.Count >= limit) break;
        }

        return cast;
    }

    /// <summary>
    /// Which season a show's page opens on: the one holding the next unwatched episode, so 播放
    /// continues the show rather than restarting it. A fully watched show falls back to its first
    /// season, and a show with no seasons at all answers null instead of throwing.
    /// <para>
    /// 特辑 is skipped while any numbered season exists. The server sorts season 0 to the front, so
    /// taking the first season with something left in it would open a show on its extras whenever those
    /// happen to be unwatched — which for most shows is always. A show whose only rows are specials is
    /// still allowed to open on them, because then there is nothing else to open on.
    /// </para>
    /// </summary>
    public static EmbyItem? PickSeason(IReadOnlyList<EmbyItem> seasons)
    {
        if (seasons.Count == 0) return null;

        // A season with no number at all counts as numbered: 「unnumbered」 is a metadata gap, and the
        // one thing known about it is that the server did not call it season zero.
        var numbered = seasons.Where(season => season.IndexNumber is not 0).ToList();
        var pool = numbered.Count > 0 ? numbered : seasons;

        return pool.FirstOrDefault(season => season.UserData?.UnplayedItemCount > 0) ?? pool[0];
    }

    /// <summary>
    /// Which episode 播放 starts on a show's page: the first unwatched one, else the first. Null for an
    /// empty list, which is a show whose season has no episodes on disk yet.
    /// </summary>
    public static EmbyItem? PickEpisode(IReadOnlyList<EmbyItem> episodes) =>
        episodes.FirstOrDefault(episode => episode.UserData?.Played != true) ?? episodes.FirstOrDefault();

    /// <summary>
    /// 「12 集，已看 3 集」, or empty for a season with nothing in it. Its own function because both
    /// headings that state it — the shelf's on an episode page and the season page's own — have to say
    /// it the same way, and because it is the one part of either heading worth a test.
    /// </summary>
    public static string EpisodeTally(IReadOnlyList<EmbyItem> episodes)
    {
        if (episodes.Count == 0) return "";

        var played = episodes.Count(episode => episode.UserData?.Played == true);
        return $"{episodes.Count} 集，已看 {played} 集";
    }

    /// <summary>
    /// The episode shelf's heading, which names the season and states how much of it is watched — the
    /// two things worth knowing before deciding whether to open the list.
    /// </summary>
    public static string EpisodeHeading(string? seasonName, IReadOnlyList<EmbyItem> episodes)
    {
        var tally = EpisodeTally(episodes);
        if (tally.Length == 0) return "更多单集";

        return seasonName is { Length: > 0 } name ? $"更多来自：{name}  ·  {tally}" : $"更多单集  ·  {tally}";
    }

    /// <summary>
    /// An episode list row's own line: 「1. 集名」. The number goes in front of the name rather than on a
    /// line of its own because the list is read down the left edge, and 「1.」 above 「集名」 doubles the
    /// row height to say one thing.
    /// </summary>
    public static string EpisodeRowTitle(EmbyItem episode) =>
        episode.IndexNumber is { } index ? $"{index}. {episode.Name}" : episode.Name;

    /// <summary>
    /// The dim line under a row's title: 「2016/4/17  ·  46 分钟」. Only the parts the server sent, joined,
    /// so a browse response that carries no premiere date reads 「46 分钟」 rather than 「  ·  46 分钟」.
    /// </summary>
    public static string EpisodeRowInfo(EmbyItem episode)
    {
        var parts = new List<string>(2);

        if (episode.PremiereDate is { } premiere)
            parts.Add(premiere.ToLocalTime().ToString("yyyy/M/d", CultureInfo.InvariantCulture));

        if (TimeFormat.Duration(episode.RunTimeTicks) is { Length: > 0 } duration) parts.Add(duration);

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Whether this episode is the one whose page is open — the 「you are here」 mark on the 单集 list. An
    /// episode page lists its own siblings, so the list contains the page itself, and the mark is what
    /// spares the reader comparing titles with the heading to work out where in the season they are.
    /// <para>
    /// Nothing is marked on a show, season or movie page, and nothing is marked once another season has
    /// been picked from the drop-down: that list does not contain the open episode, and a list with no
    /// 「you are here」 in it is the honest answer rather than a reason to mark the first row.
    /// </para>
    /// </summary>
    public static bool IsCurrentEpisode(EmbyItem episode, EmbyItem? page) =>
        page is { Type: EmbyItemType.Episode } && episode.Id == page.Id;

    /// <summary>
    /// Whether 单集 is drawn down the page as a list of rows rather than across it as a strip of cards —
    /// 「只有季页的集使用竖置列表列表就好，另外两个页面使用竖置翻页」.
    /// <para>
    /// A season page is a season's own contents: it is read top to bottom, one row per episode, with the
    /// date, the length and the first lines of the synopsis — the list *is* the page. On a series page and
    /// on an episode's own page the same episodes are context beside something else (all the seasons, or
    /// the file you opened), and a band one card tall that pages sideways is what belongs there.
    /// </para>
    /// <para>
    /// A function of the type alone, and here rather than in the page, because 电影、剧、季、集 share one
    /// detail page: the shape has to be derivable from the item, or a change meant for one of the four
    /// silently applies to all of them. That is exactly how the list leaked onto the other two pages.
    /// </para>
    /// </summary>
    public static bool EpisodesAsList(string? type) => type == EmbyItemType.Season;

    /// <summary>
    /// What a card in the paging strip says under the still: 「第 7 集  ·  46 分钟」, or only the part the
    /// server sent. The strip has no room for the row list's date-and-length line and its two lines of
    /// synopsis, so the number and the length are what is left worth saying.
    /// <para>
    /// Not <see cref="EmbyItem.CardSubtitle"/>, which every other card falls back to: that reads
    /// 「S02E09  ·  99.9 刑事专业律师」, and both halves are already on the page — the heading names the
    /// season and the page names the show. An episode the server numbered nowhere and timed nowhere gets a
    /// blank second line rather than the show's name repeated once per card.
    /// </para>
    /// </summary>
    public static string EpisodeCardSubtitle(EmbyItem episode)
    {
        var parts = new List<string>(2);

        if (episode.IndexNumber is { } index) parts.Add($"第 {index} 集");
        if (TimeFormat.Duration(episode.RunTimeTicks) is { Length: > 0 } duration) parts.Add(duration);

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Which card the strip should open on: the episode whose page this is, or 0 for 「leave it at the
    /// start」. An episode page lists its own siblings, and S02E09 is the ninth card — a strip that opens
    /// at the first one makes the reader page across the season to find where they are.
    /// <para>
    /// The same claim as <see cref="IsCurrentEpisode"/> and written in terms of it, so the row list's
    /// 「you are here」 bar and the strip's landing spot can never disagree. That also settles the case an
    /// index alone cannot express: a series page, a movie page and a season switched in the drop-down have
    /// no 「this one」 at all, and 0 there means 「don't move」 rather than 「the first episode」.
    /// </para>
    /// </summary>
    public static int EpisodeFocus(IReadOnlyList<EmbyItem> episodes, EmbyItem? page)
    {
        for (var index = 0; index < episodes.Count; index++)
            if (IsCurrentEpisode(episodes[index], page)) return index;

        return 0;
    }

    /// <summary>
    /// What a season card says under its name: 「10 集」, or nothing when the server did not count them.
    /// </summary>
    public static string SeasonSubtitle(EmbyItem season) =>
        season.ChildCount is { } episodes and > 0 ? $"{episodes} 集" : "";

    /// <summary>
    /// Which series and season the episode shelf should ask for, given the page's own item. All three
    /// entry points land here: a show uses the season that was picked, a season uses itself, and an
    /// episode uses the season it belongs to — an episode page shows its own siblings, which is what
    /// anyone finishing an episode is looking for.
    /// </summary>
    public static (string SeriesId, string? SeasonId, string? SeasonName) EpisodeScope(EmbyItem item, EmbyItem? season)
    {
        var seriesId = item.Type == EmbyItemType.Series ? item.Id : item.SeriesId ?? item.Id;
        var seasonId = season?.Id ?? (item.Type == EmbyItemType.Season ? item.Id : item.SeasonId);
        var seasonName = season?.Name ?? (item.Type == EmbyItemType.Season ? item.Name : item.SeasonName);

        return (seriesId, seasonId, seasonName);
    }

    /// <summary>
    /// Emby's own web page for the item — the way to reach the handful of fields this client cannot
    /// edit.
    /// <para>
    /// The api base is not the web root: it usually ends in <c>/emby</c>, and the web app sits one
    /// level up. Stripping that suffix is the whole trick, and it has to be case-insensitive because
    /// the address came from something a person typed.
    /// </para>
    /// </summary>
    public static string WebUrl(Uri apiBase, EmbyItem item)
    {
        var root = apiBase.GetLeftPart(UriPartial.Authority) + apiBase.AbsolutePath.TrimEnd('/');
        if (root.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) root = root[..^"/emby".Length];

        var url = $"{root}/web/index.html#!/item?id={Uri.EscapeDataString(item.Id)}";
        if (item.ServerId is { Length: > 0 } serverId) url += $"&serverId={Uri.EscapeDataString(serverId)}";

        return url;
    }
}
