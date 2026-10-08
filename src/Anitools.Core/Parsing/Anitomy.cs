using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Anitools.Core.Parsing;

/// <summary>
/// Разбор имени аниме-файла — порт встроенного anitomy оригинала (py:41–434) один в один.
/// <c>Anitomy.Parse("[Group] Title - 05 [1080p].mkv")</c> → тайтл «Title», серия «5», группа «Group», …
/// Все значения — строки (номера без ведущих нулей), как в оригинале.
/// </summary>
public static partial class Anitomy
{
    /// <summary>Словари ключевых слов (_AT_KEYWORDS) — в том же порядке, что в оригинале.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Keywords { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        ["anime_type"] =
        [
            "OVA", "ONA", "OAD", "OAV", "SP", "SPECIAL", "SPECIALS",
            "Movie", "Movies", "MOVIE", "MOVIES",
            "TV",
            "NCED", "NCOP", "OP", "ED", "CM", "PV", "Preview",
            "Gekijouban", "Tokubetsu", "Special Episode",
        ],
        ["audio_term"] =
        [
            "2CH", "2.0CH", "5.1", "5.1CH", "7.1", "7.1CH",
            "DTS", "DTS-MA", "DTS-ES", "DTS-HD", "DTS:X",
            "FLAC", "FLAC5.1", "FLAC7.1",
            "AAC", "AAC2.0", "AAC5.1",
            "AC3", "DD2.0", "DD5.1", "DD7.1", "Dolby", "TrueHD",
            "MP3", "OGG", "VORBIS",
            "HE-AAC", "LC-AAC", "EAC3",
            "Dual Audio", "Dual-Audio", "DualAudio",
            "Multi-Audio", "MultiAudio",
            "Commentary",
        ],
        ["video_term"] =
        [
            "8bit", "8-bit", "10bit", "10-bit", "10bits", "Hi10",
            "Hi444", "Hi444P", "Hi444PP",
            "H264", "H.264", "x264", "AVC",
            "H265", "H.265", "x265", "HEVC",
            "AV1", "VP8", "VP9",
            "Xvid", "DivX",
            "HDR", "HDR10", "HDR10+", "HLG", "DV", "Dolby Vision",
            "BT.709", "BT.2020",
            "60FPS", "120FPS", "24FPS",
        ],
        ["video_resolution"] =
        [
            "480p", "480i", "576p", "576i",
            "720p", "720i", "1080p", "1080i",
            "2160p", "4K", "UHD", "FHD", "HD", "SD",
            "1280x720", "1920x1080", "3840x2160",
        ],
        ["source"] =
        [
            "BD", "Blu-ray", "BluRay", "BLURAY",
            "DVD", "DVDRIP", "DVDRip",
            "HDTV", "HDTVRip",
            "WEB", "WEB-DL", "WEBDL", "WEBRip", "WEBRIP",
            "Crunchyroll", "Funimation", "Amazon", "Netflix",
            "VHSRip", "VHS",
            "LaserDisc", "LD",
        ],
        ["subtitles"] =
        [
            "ASS", "SSA", "SRT", "PGS", "SUB", "VOBSUB",
            "Soft Subs", "SoftSubs", "Softsubs",
            "Hard Subs", "HardSubs", "Hardsubs",
            "Multi-Subs", "MultiSubs",
            "Dubbed", "Subbed",
        ],
        ["file_extension"] =
        [
            "mkv", "mk3d", "mka", "mks",
            "mp4", "m4v", "m4a",
            "avi", "divx",
            "flv", "f4v",
            "mov", "qt",
            "wmv", "asf",
            "ogv", "ogg",
            "ts", "m2ts", "m2t", "mts",
            "rmvb", "rm", "3gp",
        ],
        ["language"] =
        [
            "RUS", "ENG", "JPN", "CHI", "KOR", "SPA", "POR", "FRA",
            "GER", "ITA", "ARA", "THA", "VIE",
            "Russian", "English", "Japanese", "Chinese",
            "RAW",
        ],
        // Префиксы серии. Vol/Part/Ch сюда НЕ входят: "Final Season Part 2 - 05" — серия 5, а не 2
        ["episode_prefix"] =
        [
            "Ep", "Ep.", "EP", "EP.", "Episode", "Episodes",
            "E", "EPS", "Eps",
            "SP", "OVA", "ONA", "Movie",
        ],
        ["season_prefix"] = ["S", "Season", "Seasons", "Saison"],
    };

    private static readonly FrozenDictionary<string, FrozenSet<string>> Lower =
        Keywords.ToFrozenDictionary(k => k.Key, k => k.Value.Select(TextUtils.Lower).ToFrozenSet(StringComparer.Ordinal));

    private static readonly FrozenSet<string> AllKeywords =
        new[] { "anime_type", "audio_term", "video_term", "video_resolution", "source", "subtitles", "file_extension", "language" }
            .SelectMany(k => Lower[k])
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] ResolutionWords = ["4K", "UHD", "FHD", "HD", "SD"];

    /// <summary>Разбирает имя файла. Пустая строка → пустой результат (как {} в оригинале).</summary>
    public static AnitomyResult Parse(string? inputName)
    {
        if (string.IsNullOrEmpty(inputName))
        {
            return AnitomyResult.Empty;
        }

        var filename = TextUtils.Strip(inputName);
        string? fileExtension = null;
        var ext = ExtensionRegex().Match(filename);
        if (ext.Success && Lower["file_extension"].Contains(TextUtils.Lower(ext.Groups[1].Value)))
        {
            fileExtension = TextUtils.Lower(ext.Groups[1].Value);
            filename = filename[..^ext.Length];
        }

        var tokens = Tokenize(filename);
        var found = new Found();

        // ── Шаг 1: токены в скобках (release group, технические теги) ──
        var bracketGroups = new List<(List<string> Words, bool IsFirst)>();
        string? bracketEpCandidate = null; // одиночное число в скобках — только запасной вариант
        var startsWithBracket = tokens.Count > 0 && tokens[0].Type == TokenType.Open;
        List<string>? cur = null;
        var curIsFirst = false;
        foreach (var tok in tokens)
        {
            if (tok.Type == TokenType.Open)
            {
                cur = [];
                curIsFirst = bracketGroups.Count == 0;
            }
            else if (tok.Type == TokenType.Close)
            {
                if (cur is not null)
                {
                    bracketGroups.Add((cur, curIsFirst));
                    cur = null;
                }
            }
            else if (cur is not null && tok.Type == TokenType.Unknown)
            {
                cur.Add(tok.Value);
            }
        }

        foreach (var (words, isFirst) in bracketGroups)
        {
            var combined = string.Join(" ", words);

            if (words.Count == 1 && LooksLikeYear(words[0]))
            {
                found.AnimeYear = words[0];
                continue;
            }

            if (words.Count == 1 && ChecksumRegex().IsMatch(words[0]))
            {
                found.FileChecksum = words[0];
                continue;
            }

            if (words.Count == 1 && LooksLikeResolution(words[0]))
            {
                found.VideoResolution = words[0];
                continue;
            }

            if (words.Count == 1 && IsNumeric(words[0]) && !Has(bracketEpCandidate))
            {
                var n = TextUtils.ParseInt(words[0]);
                if (n > 0 && n < 2000)
                {
                    bracketEpCandidate = words[0];
                    continue;
                }
            }

            var isTechnical = false;
            foreach (var wRaw in words)
            {
                // "1080p-FLAC" → проверяем и части, склеенные дефисом
                var parts = wRaw.Contains('-', StringComparison.Ordinal) && !IsKeyword(wRaw) && !LooksLikeResolution(wRaw)
                    ? wRaw.Split('-')
                    : [wRaw];
                foreach (var w in parts)
                {
                    var wl = TextUtils.Lower(w);
                    if (!Has(found.VideoResolution) && LooksLikeResolution(w) && !IsNumeric(w))
                    {
                        found.VideoResolution = w;
                        isTechnical = true;
                    }

                    if (Lower["video_term"].Contains(wl))
                    {
                        found.VideoTerm.Add(w);
                        isTechnical = true;
                    }

                    if (Lower["audio_term"].Contains(wl))
                    {
                        found.AudioTerm.Add(w);
                        isTechnical = true;
                    }

                    if (Lower["source"].Contains(wl))
                    {
                        found.Source = w;
                        isTechnical = true;
                    }

                    if (Lower["subtitles"].Contains(wl))
                    {
                        found.Subtitles = w;
                        isTechnical = true;
                    }

                    if (Lower["language"].Contains(wl))
                    {
                        found.Language = w;
                        isTechnical = true;
                    }

                    if (Lower["anime_type"].Contains(wl))
                    {
                        found.AnimeType = w;
                        isTechnical = true;
                    }
                }
            }

            // Первая скобочная группа в самом начале имени и не техническая — release group
            if (isFirst && startsWithBracket && !isTechnical && !Has(found.ReleaseGroup) && TextUtils.Len(combined) < 30)
            {
                found.ReleaseGroup = combined;
            }
        }

        // ── Шаг 2: токены вне скобок; AfterDash — перед токеном стоит " - " ──
        var flat = new List<(string Value, bool AfterDash)>();
        var enclosed = 0;
        for (var ti = 0; ti < tokens.Count; ti++)
        {
            var t = tokens[ti];
            if (t.Type == TokenType.Open)
            {
                enclosed++;
                continue;
            }

            if (t.Type == TokenType.Close)
            {
                enclosed = Math.Max(0, enclosed - 1);
                continue;
            }

            if (enclosed > 0 || t.Type != TokenType.Unknown)
            {
                continue;
            }

            var afterDash = false;
            for (var pj = ti - 1; pj >= 0 && tokens[pj].Type == TokenType.Delim; pj--)
            {
                if (tokens[pj].Value == "-")
                {
                    afterDash = true;
                    break;
                }
            }

            flat.Add((t.Value, afterDash));
        }

        // ── Шаг 3: номер серии и сезона ──
        var used = new HashSet<int>();
        var epIdx = -1;
        // Если чисел после " - " несколько ("Title - 2 - 05") — серия последнее из них
        var lastDashNum = -1;
        for (var j = 0; j < flat.Count; j++)
        {
            if (flat[j].AfterDash && IsNumeric(flat[j].Value))
            {
                lastDashNum = j;
            }
        }

        var typeIdx = -1; // позиция OVA/Movie/... — число перед ним не серия ("Title 2 - OVA")

        var i = 0;
        while (i < flat.Count)
        {
            var (v, afterDash) = flat[i];
            var vl = TextUtils.Lower(v);
            (string Value, bool AfterDash)? nxt = i + 1 < flat.Count ? flat[i + 1] : null;
            Match m;

            // S01E02
            m = SeasonEpisodeRegex().Match(v);
            if (m.Success)
            {
                found.AnimeSeason = TextUtils.IntString(m.Groups[1].Value);
                found.EpisodeNumber = TextUtils.IntString(m.Groups[2].Value);
                epIdx = i;
                used.Add(i);
                i++;
                continue;
            }

            // S01
            m = SeasonRegex().Match(v);
            if (m.Success)
            {
                found.AnimeSeason = TextUtils.IntString(m.Groups[1].Value);
                used.Add(i);
                i++;
                continue;
            }

            // "2nd Season"
            m = OrdinalRegex().Match(v);
            if (m.Success && nxt is { } n1 && TextUtils.Lower(n1.Value) == "season" && !Has(found.AnimeSeason))
            {
                found.AnimeSeason = TextUtils.IntString(m.Groups[1].Value);
                used.Add(i);
                used.Add(i + 1);
                i += 2;
                continue;
            }

            // Season 2 (номер после " - " — это уже серия: "2nd Season - 05")
            if (Lower["season_prefix"].Contains(vl) && nxt is { } n2 && IsNumeric(n2.Value) && !n2.AfterDash)
            {
                found.AnimeSeason = TextUtils.IntString(n2.Value);
                used.Add(i);
                used.Add(i + 1);
                i += 2;
                continue;
            }

            // Ep01, E05, Episode5
            m = PrefixedEpisodeRegex().Match(v);
            if (m.Success && !Has(found.EpisodeNumber))
            {
                found.EpisodeNumber = TextUtils.IntString(m.Groups[1].Value);
                epIdx = i;
                used.Add(i);
                i++;
                continue;
            }

            // Ep 01, Episode 5
            if (Lower["episode_prefix"].Contains(vl) && !Has(found.EpisodeNumber) && nxt is { } n3 && IsNumeric(n3.Value))
            {
                found.EpisodeNumber = TextUtils.IntString(n3.Value);
                // "OVA 2", "Movie 3" — это ещё и тип релиза
                if (!Has(found.AnimeType) && Lower["anime_type"].Contains(vl))
                {
                    found.AnimeType = v;
                }

                epIdx = i + 1;
                used.Add(i);
                used.Add(i + 1);
                i += 2;
                continue;
            }

            // 01v2
            m = VersionedEpisodeRegex().Match(v);
            if (m.Success && !Has(found.EpisodeNumber))
            {
                found.EpisodeNumber = TextUtils.IntString(m.Groups[1].Value);
                found.ReleaseVersion = "v" + m.Groups[2].Value;
                epIdx = i;
                used.Add(i);
                i++;
                continue;
            }

            // 01-12, 01~12
            m = EpisodeRangeRegex().Match(v);
            if (m.Success && !Has(found.EpisodeNumber))
            {
                found.EpisodeNumber = TextUtils.IntString(m.Groups[1].Value);
                found.EpisodeNumberAlt = TextUtils.IntString(m.Groups[2].Value);
                epIdx = i;
                used.Add(i);
                i++;
                continue;
            }

            // 第01話
            m = JapaneseEpisodeRegex().Match(v);
            if (m.Success && !Has(found.EpisodeNumber))
            {
                found.EpisodeNumber = TextUtils.IntString(m.Groups[1].Value);
                epIdx = i;
                used.Add(i);
                i++;
                continue;
            }

            if (LooksLikeYear(v) && !Has(found.AnimeYear))
            {
                found.AnimeYear = v;
                used.Add(i);
                i++;
                continue;
            }

            if (LooksLikeResolution(v) && !Has(found.VideoResolution))
            {
                found.VideoResolution = v;
                used.Add(i);
                i++;
                continue;
            }

            // Отдельная версия релиза: "Title - 01 v2"
            if (VersionRegex().IsMatch(v) && Has(found.EpisodeNumber) && !Has(found.ReleaseVersion))
            {
                found.ReleaseVersion = vl;
                used.Add(i);
                i++;
                continue;
            }

            // Число после " - " — серия (главный паттерн). "- 00" тоже серия (пролог)
            if (afterDash && IsNumeric(v) && i == lastDashNum && !Has(found.EpisodeNumber))
            {
                var n = TextUtils.ParseInt(v);
                if (n >= 0 && n < 2000)
                {
                    found.EpisodeNumber = n.ToString(CultureInfo.InvariantCulture);
                    epIdx = i;
                    used.Add(i);
                    i++;
                    continue;
                }
            }

            // Техническое ключевое слово
            if (IsKeyword(v))
            {
                if (!Has(found.AnimeType) && Lower["anime_type"].Contains(vl))
                {
                    found.AnimeType = v;
                    typeIdx = i;
                }

                used.Add(i);
                i++;
                continue;
            }

            i++;
        }

        // Серия не найдена — последнее изолированное число (но не перед OVA/Movie)
        if (!Has(found.EpisodeNumber))
        {
            for (var k = flat.Count - 1; k > typeIdx; k--)
            {
                var v = flat[k].Value;
                if (IsNumeric(v) && !used.Contains(k) && TextUtils.ParseInt(v) is var n && n > 0 && n < 2000)
                {
                    found.EpisodeNumber = n.ToString(CultureInfo.InvariantCulture);
                    epIdx = k;
                    used.Add(k);
                    break;
                }
            }
        }

        if (!Has(found.EpisodeNumber) && Has(bracketEpCandidate))
        {
            found.EpisodeNumber = TextUtils.IntString(bracketEpCandidate!);
        }

        // ── Шаг 4: название — всё, что до первого распознанного токена ──
        var firstUsed = used.Count > 0 ? used.Min() : flat.Count;
        var title = TextUtils.Strip(string.Join(" ", Enumerable.Range(0, firstUsed).Where(k => !used.Contains(k)).Select(k => flat[k].Value)));
        title = TrailingSpaceRegex().Replace(title, "");
        title = TextUtils.Strip(TrailingDashRegex().Replace(title, ""));
        if (!title.Contains(' ', StringComparison.Ordinal) && title.Contains('_', StringComparison.Ordinal))
        {
            title = title.Replace('_', ' ');
        }

        if (title.Length > 0)
        {
            found.AnimeTitle = title;
        }

        // ── Шаг 5: название серии — токены после номера серии ──
        if (Has(found.EpisodeNumber))
        {
            var lastUsed = epIdx >= 0 ? epIdx : (used.Count > 0 ? used.Max() : -1);
            var after = Enumerable.Range(lastUsed + 1, Math.Max(0, flat.Count - lastUsed - 1))
                .Where(k => !used.Contains(k) && !IsKeyword(flat[k].Value))
                .Select(k => flat[k].Value);
            var epTitle = TextUtils.Strip(LeadingDashRegex().Replace(TextUtils.Strip(string.Join(" ", after)), ""));
            if (TextUtils.Len(epTitle) > 1)
            {
                found.EpisodeTitle = epTitle;
            }
        }

        return new AnitomyResult
        {
            FileName = filename,
            FileExtension = fileExtension,
            ReleaseGroup = NullIfEmpty(found.ReleaseGroup),
            AnimeTitle = NullIfEmpty(found.AnimeTitle),
            AnimeYear = NullIfEmpty(found.AnimeYear),
            AnimeType = NullIfEmpty(found.AnimeType),
            AnimeSeason = NullIfEmpty(found.AnimeSeason),
            EpisodeNumber = NullIfEmpty(found.EpisodeNumber),
            EpisodeNumberAlt = NullIfEmpty(found.EpisodeNumberAlt),
            EpisodeTitle = NullIfEmpty(found.EpisodeTitle),
            ReleaseVersion = NullIfEmpty(found.ReleaseVersion),
            VideoResolution = NullIfEmpty(found.VideoResolution),
            VideoTerm = found.VideoTerm.Count > 0 ? string.Join(", ", found.VideoTerm) : null,
            AudioTerm = found.AudioTerm.Count > 0 ? string.Join(", ", found.AudioTerm) : null,
            Source = NullIfEmpty(found.Source),
            Subtitles = NullIfEmpty(found.Subtitles),
            Language = NullIfEmpty(found.Language),
            FileChecksum = NullIfEmpty(found.FileChecksum),
        };
    }

    // ── Токены ──

    private enum TokenType
    {
        Open,
        Close,
        Delim,
        Unknown,
    }

    private readonly record struct Token(TokenType Type, string Value);

    /// <summary>Разбивает строку на токены по разделителям и скобкам (_at_tokenize).</summary>
    private static List<Token> Tokenize(string filename)
    {
        var tokens = new List<Token>();
        var buf = new System.Text.StringBuilder();

        void Flush()
        {
            if (buf.Length > 0)
            {
                tokens.Add(new Token(TokenType.Unknown, buf.ToString()));
                buf.Clear();
            }
        }

        for (var i = 0; i < filename.Length; i++)
        {
            var ch = filename[i];
            if ("([{「【『（〔".Contains(ch, StringComparison.Ordinal))
            {
                Flush();
                tokens.Add(new Token(TokenType.Open, ch.ToString()));
            }
            else if (")]}」】』）〕".Contains(ch, StringComparison.Ordinal))
            {
                Flush();
                tokens.Add(new Token(TokenType.Close, ch.ToString()));
            }
            else if (" _.,|".Contains(ch, StringComparison.Ordinal))
            {
                Flush();
                tokens.Add(new Token(TokenType.Delim, ch.ToString()));
            }
            else if (ch is '–' or '—')
            {
                // Длинное тире — всегда разделитель, нормализуем к "-"
                Flush();
                tokens.Add(new Token(TokenType.Delim, "-"));
            }
            else if (ch == '-')
            {
                // Дефис — разделитель, если рядом пробел
                var prev = i > 0 ? filename[i - 1] : '\0';
                var next = i + 1 < filename.Length ? filename[i + 1] : '\0';
                if (prev == ' ' || next == ' ')
                {
                    Flush();
                    tokens.Add(new Token(TokenType.Delim, "-"));
                }
                else
                {
                    buf.Append(ch);
                }
            }
            else
            {
                buf.Append(ch);
            }
        }

        Flush();
        return tokens;
    }

    private static bool IsNumeric(string s) => NumericRegex().IsMatch(s);

    private static bool LooksLikeYear(string s) =>
        IsNumeric(s) && s.Length == 4 && TextUtils.ParseInt(s) is var n && n >= 1950 && n <= 2050;

    private static bool LooksLikeResolution(string s) =>
        ResolutionPRegex().IsMatch(s) || ResolutionXRegex().IsMatch(s) || ResolutionWords.Contains(s.ToUpperInvariant());

    private static bool IsKeyword(string s) => AllKeywords.Contains(TextUtils.Lower(s));

    private static bool Has(string? s) => !string.IsNullOrEmpty(s);

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private sealed class Found
    {
        public string? ReleaseGroup { get; set; }

        public string? AnimeTitle { get; set; }

        public string? AnimeYear { get; set; }

        public string? AnimeType { get; set; }

        public string? AnimeSeason { get; set; }

        public string? EpisodeNumber { get; set; }

        public string? EpisodeNumberAlt { get; set; }

        public string? EpisodeTitle { get; set; }

        public string? ReleaseVersion { get; set; }

        public string? VideoResolution { get; set; }

        public List<string> VideoTerm { get; } = [];

        public List<string> AudioTerm { get; } = [];

        public string? Source { get; set; }

        public string? Subtitles { get; set; }

        public string? Language { get; set; }

        public string? FileChecksum { get; set; }
    }

    // ── Регулярки оригинала (Python $ = .NET $: конец строки или перед последним \n) ──

    [GeneratedRegex(@"\.([a-zA-Z0-9]{2,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionRegex();

    [GeneratedRegex(@"\A[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericRegex();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumRegex();

    [GeneratedRegex(@"\A[0-9]{3,4}[pi]\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPRegex();

    [GeneratedRegex(@"\A[0-9]{3,4}x[0-9]{3,4}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionXRegex();

    [GeneratedRegex(@"\A[Ss]([0-9]{1,2})[Ee]([0-9]{1,3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\A[Ss]([0-9]{1,2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonRegex();

    [GeneratedRegex(@"\A([0-9]{1,2})(?:st|nd|rd|th)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalRegex();

    [GeneratedRegex(@"\A(?:e|ep\.?|eps\.?|episode)([0-9]+)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedEpisodeRegex();

    [GeneratedRegex(@"\A([0-9]+)v([0-9])\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionedEpisodeRegex();

    [GeneratedRegex(@"\A([0-9]+)[-~]([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeRangeRegex();

    [GeneratedRegex(@"\A第([0-9]{1,3})話\z", RegexOptions.CultureInvariant)]
    private static partial Regex JapaneseEpisodeRegex();

    [GeneratedRegex(@"\Av[0-9]\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"[" + TextUtils.SpaceChars + "_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSpaceRegex();

    [GeneratedRegex(@"[-–]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDashRegex();

    [GeneratedRegex(@"^[-–" + TextUtils.SpaceChars + "]+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDashRegex();
}

/// <summary>Результат <see cref="Anitomy.Parse"/>; null — поле не найдено.</summary>
public sealed record AnitomyResult
{
    public static AnitomyResult Empty { get; } = new();

    /// <summary>Имя без расширения и пробелов по краям; null только у пустого входа.</summary>
    public string? FileName { get; init; }

    public string? FileExtension { get; init; }

    public string? ReleaseGroup { get; init; }

    public string? AnimeTitle { get; init; }

    public string? AnimeYear { get; init; }

    /// <summary>Тип релиза как в имени файла: OVA, Movie, SP, …</summary>
    public string? AnimeType { get; init; }

    public string? AnimeSeason { get; init; }

    /// <summary>Номер серии без ведущих нулей: «5», «0», «1100».</summary>
    public string? EpisodeNumber { get; init; }

    public string? EpisodeNumberAlt { get; init; }

    public string? EpisodeTitle { get; init; }

    public string? ReleaseVersion { get; init; }

    public string? VideoResolution { get; init; }

    public string? VideoTerm { get; init; }

    public string? AudioTerm { get; init; }

    public string? Source { get; init; }

    public string? Subtitles { get; init; }

    public string? Language { get; init; }

    public string? FileChecksum { get; init; }

    /// <summary>Словарь с ключами оригинала (file_name, anime_title, …) — только найденные поля.</summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var d = new Dictionary<string, string>();
        if (FileName is null)
        {
            return d;
        }

        d["file_name"] = FileName;
        Add("file_extension", FileExtension);
        Add("release_group", ReleaseGroup);
        Add("anime_title", AnimeTitle);
        Add("anime_year", AnimeYear);
        Add("anime_type", AnimeType);
        Add("anime_season", AnimeSeason);
        Add("episode_number", EpisodeNumber);
        Add("episode_number_alt", EpisodeNumberAlt);
        Add("episode_title", EpisodeTitle);
        Add("release_version", ReleaseVersion);
        Add("video_resolution", VideoResolution);
        Add("video_term", VideoTerm);
        Add("audio_term", AudioTerm);
        Add("source", Source);
        Add("subtitles", Subtitles);
        Add("language", Language);
        Add("file_checksum", FileChecksum);
        return d;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                d[key] = value;
            }
        }
    }
}
