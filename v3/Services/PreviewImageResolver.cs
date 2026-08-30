using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CskinNative.Services;

public static class PreviewImageResolver
{
    private const string WikiApi = "https://wiki.leagueoflegends.com/en-us/api.php";
    private static readonly HttpClient WikiHttp = CreateWikiClient();
    private static readonly ConcurrentDictionary<int, Task<string?>> WikiUrls = [];

    public static async Task<Image?> LoadAsync(Skin skin, CancellationToken cancellationToken = default)
    {
        var image = await ImageCache.LoadAsync(skin.Image).ConfigureAwait(false);
        if (image is not null)
        {
            AppLog.Info($"预览图加载成功：skinId={skin.Id} source={SourceName(skin.Image)}");
            return image;
        }

        if (skin.Chroma)
        {
            foreach (var communityDragonUrl in CommunityDragonChromaPreviewUrls(skin))
            {
                if (string.Equals(communityDragonUrl, skin.Image, StringComparison.OrdinalIgnoreCase)) continue;
                image = await ImageCache.LoadAsync(communityDragonUrl).ConfigureAwait(false);
                if (image is not null)
                {
                    AppLog.Info($"预览图加载成功：skinId={skin.Id} source=communitydragon-chromapreview url={communityDragonUrl}");
                    return image;
                }
                AppLog.Warn($"CommunityDragon 炫彩预览图不可用：skinId={skin.Id} url={communityDragonUrl}");
            }

            var wikiUrl = await ResolveWikiUrlAsync(skin, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(wikiUrl))
            {
                image = await ImageCache.LoadAsync(wikiUrl).ConfigureAwait(false);
                if (image is not null)
                {
                    AppLog.Info($"预览图加载成功：skinId={skin.Id} source=league-wiki");
                    return image;
                }
                AppLog.Warn($"Wiki 预览图下载或解码失败：skinId={skin.Id}");
            }
        }

        image = await ImageCache.LoadAsync(skin.FallbackImage).ConfigureAwait(false);
        if (image is not null)
        {
            AppLog.Info($"预览图回退基础皮肤：skinId={skin.Id} source={SourceName(skin.FallbackImage)}");
            return image;
        }

        AppLog.Warn($"预览图不可用：skinId={skin.Id} chroma={skin.Chroma} primary={skin.Image}");
        return null;
    }

    public static void Clear() => WikiUrls.Clear();

    private static async Task<string?> ResolveWikiUrlAsync(Skin skin, CancellationToken cancellationToken)
    {
        if (WikiUrls.TryGetValue(skin.Id, out var existing))
            return await existing.WaitAsync(cancellationToken).ConfigureAwait(false);
        var task = ResolveWikiUrlCoreAsync(skin);
        var cached = WikiUrls.GetOrAdd(skin.Id, task);
        return await cached.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ResolveWikiUrlCoreAsync(Skin skin)
    {
        var color = ChromaColorName(skin);
        var baseName = BaseName(!string.IsNullOrWhiteSpace(skin.EnglishName) ? skin.EnglishName : skin.Name);

        var pageImage = await ResolveWikiPageImageAsync(skin, baseName, color).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(pageImage))
        {
            AppLog.Info($"Wiki 皮肤页解析成功：skinId={skin.Id} url={pageImage}");
            return pageImage;
        }

        var queries = skin.Slug.StartsWith("Champion", StringComparison.OrdinalIgnoreCase)
            ? new[] { $"{baseName} {color}", $"{baseName} chroma {color}" }
            : new[]
            {
                $"{baseName} {skin.Slug} {color}",
                $"{skin.Slug} {baseName} {color}",
                $"{skin.Slug} {color}"
            };
        queries = queries.Where(query => !string.IsNullOrWhiteSpace(query)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var query in queries)
        {
            try
            {
                var uri = new UriBuilder(WikiApi);
                var parameters = new Dictionary<string, string>
                {
                    ["action"] = "query", ["generator"] = "search", ["gsrsearch"] = query,
                    ["gsrnamespace"] = "6", ["gsrlimit"] = "20", ["prop"] = "imageinfo",
                    ["iiprop"] = "url|mime|size", ["format"] = "json", ["formatversion"] = "2"
                };
                uri.Query = string.Join('&', parameters.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
                using var response = await WikiHttp.GetAsync(uri.Uri).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Warn($"Wiki 预览图搜索失败：skinId={skin.Id} status={(int)response.StatusCode}");
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                var candidate = SelectCandidate(document.RootElement, skin, color, baseName);
                if (candidate is not null)
                {
                    AppLog.Info($"Wiki 预览图解析成功：skinId={skin.Id} query={query}");
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                AppLog.Warn($"Wiki 预览图解析异常：skinId={skin.Id} error={ex.Message}");
            }
        }

        AppLog.Warn($"Wiki 未找到匹配的炫彩预览图：skinId={skin.Id} name={skin.Name}");
        return null;
    }

    private static async Task<string?> ResolveWikiPageImageAsync(Skin skin, string baseName, string color)
    {
        var pageUrl = WikiPageUrl(skin, baseName);
        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            AppLog.Info($"Wiki 皮肤页跳过：skinId={skin.Id} 缺少英文皮肤名或英雄 slug");
            return null;
        }

        try
        {
            using var response = await WikiHttp.GetAsync(pageUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"Wiki 皮肤页请求失败：skinId={skin.Id} status={(int)response.StatusCode}");
                return null;
            }

            var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var wantedColor = Normalize(color);
            var pageBase = baseName;
            var championSuffix = " " + skin.Slug;
            if (pageBase.EndsWith(championSuffix, StringComparison.OrdinalIgnoreCase))
                pageBase = pageBase[..^championSuffix.Length].Trim();
            var wantedBase = Normalize(pageBase);
            var wantedChampion = Normalize(skin.Slug);

            foreach (Match match in Regex.Matches(
                         html,
                         "href=[\\\"'](?<href>/en-us/File:[^\\\"']+\\.(?:png|jpg|jpeg))[\\\"']",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
                var fileName = href[(href.IndexOf(':') + 1)..];
                fileName = Uri.UnescapeDataString(fileName);
                var normalizedFile = Normalize(fileName);
                if (!normalizedFile.Contains(wantedChampion, StringComparison.Ordinal)
                    || !normalizedFile.Contains(wantedBase, StringComparison.Ordinal)
                    || !normalizedFile.Contains(wantedColor, StringComparison.Ordinal)) continue;

                var imagePath = "/en-us/images/" + Uri.EscapeDataString(fileName);
                return new Uri(new Uri(WikiApi), imagePath).AbsoluteUri;
            }

            AppLog.Warn($"Wiki 皮肤页未找到炫彩图片：skinId={skin.Id} page={pageUrl} color={color}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn($"Wiki 皮肤页解析异常：skinId={skin.Id} error={ex.Message}");
        }

        return null;
    }

    private static string? WikiPageUrl(Skin skin, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)
            || string.IsNullOrWhiteSpace(skin.Slug)
            || skin.Slug.StartsWith("Champion", StringComparison.OrdinalIgnoreCase)) return null;

        var title = baseName.Trim();
        var suffix = " " + skin.Slug;
        if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            title = title[..^suffix.Length].Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;
        return $"https://wiki.leagueoflegends.com/en-us/{Uri.EscapeDataString(skin.Slug)}/Cosmetics/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
    }

    private static IEnumerable<string> CommunityDragonChromaPreviewUrls(Skin skin)
    {
        if (!skin.Chroma || skin.Id <= 0 || string.IsNullOrWhiteSpace(skin.Slug)
            || skin.Slug.StartsWith("Champion", StringComparison.OrdinalIgnoreCase)) yield break;
        var skinSlot = skin.Id % 1000;
        if (skinSlot <= 0) yield break;

        // raw.communitydragon.org paths are case-sensitive even though the
        // champion slug in Riot metadata is normally PascalCase.
        var champion = Uri.EscapeDataString(skin.Slug.ToLowerInvariant());
        yield return $"https://raw.communitydragon.org/latest/game/assets/characters/{champion}/skins/skin{skinSlot}/chromapreview.png";
        yield return $"https://raw.communitydragon.org/pbe/game/assets/characters/{champion}/skins/skin{skinSlot}/chromapreview.png";
    }

    private static string? SelectCandidate(JsonElement root, Skin skin, string color, string baseName)
    {
        if (!root.TryGetProperty("query", out var query)
            || !query.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array) return null;
        var championToken = Normalize(skin.Slug);
        var colorToken = Normalize(color);
        var baseTokens = baseName
            .Split([' ', '\t', ':', '-', '_', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidates = new List<(string Url, int Score)>();
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("title", out var titleValue)
                || !page.TryGetProperty("imageinfo", out var imageInfo)
                || imageInfo.ValueKind != JsonValueKind.Array || imageInfo.GetArrayLength() == 0) continue;
            var title = titleValue.GetString() ?? "";
            var normalizedTitle = Normalize(title);
            if (!skin.Slug.StartsWith("Champion", StringComparison.OrdinalIgnoreCase)
                && !normalizedTitle.Contains(championToken, StringComparison.Ordinal)
                || !normalizedTitle.Contains(colorToken, StringComparison.Ordinal)) continue;
            if (baseTokens.Length == 0 || baseTokens.Any(token => !normalizedTitle.Contains(token, StringComparison.Ordinal))) continue;
            if (!imageInfo[0].TryGetProperty("url", out var urlValue)) continue;
            var candidate = urlValue.GetString();
            if (!IsAllowedWikiImage(candidate)) continue;
            var score = 10;
            score += baseTokens.Length * 5;
            var englishToken = Normalize(skin.EnglishName ?? "");
            var localizedToken = Normalize(skin.Name);
            if ((!string.IsNullOrWhiteSpace(englishToken) && normalizedTitle.Contains(englishToken, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(localizedToken) && normalizedTitle.Contains(localizedToken, StringComparison.Ordinal))) score += 10;
            candidates.Add((candidate!, score));
        }
        return candidates.OrderByDescending(candidate => candidate.Score).Select(candidate => candidate.Url).FirstOrDefault();
    }

    private static bool IsAllowedWikiImage(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("wiki.leagueoflegends.com", StringComparison.OrdinalIgnoreCase);

    private static string BaseName(string? value)
    {
        var name = value?.Trim() ?? "";
        var open = name.IndexOf('(');
        if (open < 0) open = name.IndexOf('（');
        return (open > 0 ? name[..open] : name).Trim();
    }

    private static string ChromaColorName(Skin skin)
    {
        var name = skin.Name.Replace('（', '(').Replace('）', ')');
        var open = name.LastIndexOf('(');
        var close = name.LastIndexOf(')');
        var token = open >= 0 && close > open ? name[(open + 1)..close].Trim() : "";
        return token switch
        {
            "红宝石" => "Ruby", "猫眼" => "Catseye", "翡翠" => "Emerald", "青金石" => "Sapphire",
            "紫水晶" => "Amethyst", "绿松石" => "Turquoise", "珍珠" => "Pearl", "曜石" => "Obsidian",
            "黄宝石" => "Citrine", "玫瑰石英" => "Rose Quartz", "无限" => "Limitless",
            _ => string.IsNullOrWhiteSpace(token) ? "chroma" : token
        };
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string SourceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        if (value.StartsWith("https://raw.communitydragon.org", StringComparison.OrdinalIgnoreCase)) return "communitydragon";
        if (value.StartsWith("https://ddragon.leagueoflegends.com", StringComparison.OrdinalIgnoreCase)) return "ddragon";
        return value.StartsWith("https://wiki.leagueoflegends.com", StringComparison.OrdinalIgnoreCase) ? "league-wiki" : "local";
    }

    private static HttpClient CreateWikiClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PortableCskin/0.3 (+preview resolver)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
