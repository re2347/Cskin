using System.Text.Json;

namespace CskinNative.Services;

public sealed class LocalCatalog
{
    // Riot does not publish separate splash files for these in-client form
    // IDs. Keep the form selectable, but use the real parent skin artwork for
    // its card and inspector preview instead of requesting a guaranteed 404.
    private static readonly IReadOnlyDictionary<int, int> PreviewBaseSkinIds = new Dictionary<int, int>
    {
        [103087] = 103086,
        [99991] = 99007, [99992] = 99007, [99993] = 99007, [99994] = 99007,
        [99995] = 99007, [99996] = 99007, [99997] = 99007, [99998] = 99007, [99999] = 99007,
        [25999] = 25080,
        [222998] = 222060, [222999] = 222060,
        [37998] = 37006, [37999] = 37006,
        [21997] = 21016, [21998] = 21016, [21999] = 21016,
        [234994] = 234043, [234995] = 234043, [234996] = 234043,
        [234997] = 234043, [234998] = 234043, [234999] = 234043,
        [875998] = 875066, [875999] = 875066,
        [145999] = 145071,
        [82998] = 82054, [82999] = 82054
    };

    private static readonly IReadOnlyDictionary<int, string> SpecialFormNames = new Dictionary<int, string>
    {
        [103086] = "殿堂传奇 阿狸 · 进阶形态",
        [103087] = "殿堂传奇 阿狸 · 不朽形态",
        [99991] = "大元素使 拉克丝 · 风",
        [99992] = "大元素使 拉克丝 · 暗",
        [99993] = "大元素使 拉克丝 · 冰",
        [99994] = "大元素使 拉克丝 · 熔岩",
        [99995] = "大元素使 拉克丝 · 神秘",
        [99996] = "大元素使 拉克丝 · 自然",
        [99997] = "大元素使 拉克丝 · 风暴",
        [99998] = "大元素使 拉克丝 · 水",
        [99999] = "大元素使 拉克丝 · 火",
        [25999] = "灵魂莲华 莫甘娜 · 形态 2",
        [147002] = "K/DA ALL OUT 萨勒芬妮 明日之星",
        [147003] = "K/DA ALL OUT 萨勒芬妮 超级巨星",
        [222998] = "双城之战2 破碎者 金克丝 · 形态 2",
        [222999] = "双城之战2 破碎者 金克丝 · 形态 3",
        [37998] = "DJ 娑娜 · 形态 2",
        [37999] = "DJ 娑娜 · 形态 3",
        [21997] = "女帝 厄运小姐 · 零时",
        [21998] = "女帝 厄运小姐 · 皇家武装",
        [21999] = "女帝 厄运小姐 · 星云蜂群",
        [234994] = "王 佛耶戈 · 刺客",
        [234995] = "王 佛耶戈 · 战士",
        [234996] = "王 佛耶戈 · 法师",
        [234997] = "王 佛耶戈 · 射手",
        [234998] = "王 佛耶戈 · 辅助",
        [234999] = "王 佛耶戈 · 坦克",
        [875998] = "金蛇巨星 瑟提 · 形态 2",
        [875999] = "金蛇巨星 瑟提 · 形态 3",
        [145071] = "殿堂传奇 卡莎 · 不朽形态",
        [145999] = "殿堂传奇 卡莎 · 形态 2",
        [82998] = "萨恩 · 乌祖尔 莫德凯撒 · 形态 2",
        [82999] = "萨恩 · 乌祖尔 莫德凯撒 · 形态 3"
    };

    public int Schema { get; set; }
    public string Version { get; set; } = "";
    public List<Champion> Champions { get; set; } = [];

    public static async Task<LocalCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = AppPaths.Asset("skin_index.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await using var stream = File.OpenRead(path);
        var catalog = await JsonSerializer.DeserializeAsync<LocalCatalog>(stream, options, cancellationToken) ?? new LocalCatalog();

        var namesPath = AppPaths.Asset("skin_names_zh_CN.json");
        var names = new Dictionary<string, string>();
        if (File.Exists(namesPath))
        {
            await using var namesStream = File.OpenRead(namesPath);
            names = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(namesStream, options, cancellationToken) ?? [];
        }

        foreach (var skin in catalog.Champions.SelectMany(champion => champion.Skins))
        {
            if (names.TryGetValue(skin.Id.ToString(), out var localizedName) && !string.IsNullOrWhiteSpace(localizedName))
                skin.LocalizedName = localizedName;
            else if (SpecialFormNames.TryGetValue(skin.Id, out var specialName))
                skin.LocalizedName = specialName;
            skin.Image = PreviewImage(skin);
            skin.FallbackImage = PreviewFallbackImage(skin);
        }

        return catalog;
    }

    public int MergeRepository(
        IReadOnlyDictionary<int, string> paths,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyDictionary<int, string>? englishNames = null,
        IReadOnlyDictionary<int, string>? previewPaths = null)
    {
        if (paths.Count == 0) return 0;

        var existing = Champions.SelectMany(champion => champion.Skins).ToDictionary(skin => skin.Id);
        var owners = Champions.SelectMany(champion => champion.Skins.Select(skin => (skin.Id, Champion: champion)))
            .ToDictionary(pair => pair.Id, pair => pair.Champion);
        var champions = Champions.ToDictionary(champion => champion.Id);
        var changed = 0;

        foreach (var skin in existing.Values)
        {
            if (names.TryGetValue(skin.Id, out var name) && !string.Equals(skin.LocalizedName, name, StringComparison.Ordinal))
            {
                skin.LocalizedName = name;
                changed++;
            }
            if (englishNames is not null && englishNames.TryGetValue(skin.Id, out var englishName)
                && !string.Equals(skin.EnglishName, englishName, StringComparison.Ordinal))
            {
                skin.EnglishName = englishName;
                if (!string.Equals(skin.Name, englishName, StringComparison.Ordinal))
                    skin.Name = englishName;
                changed++;
            }
            if (previewPaths is not null && previewPaths.TryGetValue(skin.Id, out var existingPreview))
            {
                skin.LocalPreviewPath = existingPreview;
                skin.Image = existingPreview;
            }
        }

        foreach (var pair in paths)
        {
            if (existing.ContainsKey(pair.Key)) continue;
            if (!TryParseRepositoryPath(pair.Value, out var championId, out var baseSkinId)) continue;

            if (!champions.TryGetValue(championId, out var champion))
            {
                var championName = names.TryGetValue(championId * 1000, out var localizedChampion)
                    ? localizedChampion
                    : $"英雄 {championId}";
                champion = new Champion
                {
                    Id = championId,
                    Name = championName,
                    Slug = $"Champion{championId}",
                    Icon = ChampionIcon(championId)
                };
                champions[championId] = champion;
                Champions.Add(champion);
            }

            var isChroma = pair.Key != baseSkinId;
            var skin = new Skin
            {
                Id = pair.Key,
                Name = names.TryGetValue(pair.Key, out var localizedName) ? localizedName : $"皮肤 {pair.Key}",
                LocalizedName = names.TryGetValue(pair.Key, out localizedName) ? localizedName : null,
                EnglishName = englishNames is not null && englishNames.TryGetValue(pair.Key, out var englishName) ? englishName : null,
                ChampionId = championId,
                ChampionName = champion.Name,
                Slug = champion.Slug,
                Image = SkinImage(championId, pair.Key, isChroma, champion.Slug),
                FallbackImage = SkinFallbackImage(championId, pair.Key, isChroma, baseSkinId, champion.Slug),
                LocalPreviewPath = previewPaths is not null && previewPaths.TryGetValue(pair.Key, out var previewPath) ? previewPath : null,
                Chroma = isChroma,
                BaseSkinId = baseSkinId
            };
            if (!string.IsNullOrWhiteSpace(skin.LocalPreviewPath)) skin.Image = skin.LocalPreviewPath;
            champion.Skins.Add(skin);
            existing[pair.Key] = skin;
            owners[pair.Key] = champion;
            changed++;
        }

        // A repository revision may move a package to a different base-skin
        // directory or change its chroma relationship without changing the
        // numeric id. Reparent and rebuild its preview fields before pruning
        // unavailable entries so the UI grouping follows the remote tree.
        foreach (var pair in paths)
        {
            if (!existing.TryGetValue(pair.Key, out var skin)
                || !TryParseRepositoryPath(pair.Value, out var championId, out var baseSkinId)) continue;
            if (!champions.TryGetValue(championId, out var champion))
            {
                var championName = names.TryGetValue(championId * 1000, out var localizedChampion)
                    ? localizedChampion
                    : $"英雄 {championId}";
                champion = new Champion
                {
                    Id = championId,
                    Name = championName,
                    Slug = $"Champion{championId}",
                    Icon = ChampionIcon(championId)
                };
                champions[championId] = champion;
                Champions.Add(champion);
                changed++;
            }
            if (owners.TryGetValue(pair.Key, out var previousOwner) && previousOwner.Id != championId)
            {
                previousOwner.Skins.Remove(skin);
                champion.Skins.Add(skin);
                owners[pair.Key] = champion;
                changed++;
            }
            var isChroma = pair.Key != baseSkinId;
            var expectedImage = previewPaths?.GetValueOrDefault(pair.Key)
                ?? SkinImage(championId, pair.Key, isChroma, champion.Slug);
            var expectedFallback = SkinFallbackImage(championId, pair.Key, isChroma, baseSkinId, champion.Slug);
            if (skin.ChampionId != championId || skin.BaseSkinId != baseSkinId || skin.Chroma != isChroma
                || !string.Equals(skin.LocalPreviewPath, previewPaths?.GetValueOrDefault(pair.Key), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(skin.Image, expectedImage, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(skin.FallbackImage, expectedFallback, StringComparison.OrdinalIgnoreCase))
            {
                skin.ChampionId = championId;
                skin.ChampionName = champion.Name;
                skin.Slug = champion.Slug;
                skin.BaseSkinId = baseSkinId;
                skin.Chroma = isChroma;
                skin.LocalPreviewPath = previewPaths?.GetValueOrDefault(pair.Key);
                skin.Image = expectedImage;
                skin.FallbackImage = expectedFallback;
                changed++;
            }
        }

        var available = paths.Keys.ToHashSet();
        foreach (var champion in Champions)
        {
            var before = champion.Skins.Count;
            champion.Skins = champion.Skins.Where(skin => available.Contains(skin.Id)).ToList();
            champion.SkinCount = champion.Skins.Count;
            if (champion.Skins.Count != before) changed++;
        }
        Champions = Champions.Where(champion => champion.Skins.Count > 0).ToList();
        return changed;
    }

    private static bool TryParseRepositoryPath(string path, out int championId, out int baseSkinId)
    {
        championId = 0;
        baseSkinId = 0;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4
            && parts[0].Equals("skins", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out championId)
            && int.TryParse(parts[2], out baseSkinId);
    }

    private static string ChampionIcon(int championId) =>
        $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-icons/{championId}.png";

    private static string SkinImage(int championId, int skinId, bool chroma, string slug)
    {
        if (chroma)
            return $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-chroma-images/{championId}/{skinId}.png";
        skinId = PreviewBaseSkinIds.GetValueOrDefault(skinId, skinId);
        if (slug.StartsWith("Champion", StringComparison.Ordinal))
            return $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-skin-images/{championId}/{skinId}.jpg";
        return $"https://ddragon.leagueoflegends.com/cdn/img/champion/splash/{slug}_{skinId % 1000}.jpg";
    }

    private static string PreviewImage(Skin skin) =>
        SkinImage(skin.ChampionId, skin.Id, skin.Chroma, skin.Slug);

    private static string? PreviewFallbackImage(Skin skin) =>
        SkinFallbackImage(skin.ChampionId, skin.Id, skin.Chroma, skin.BaseSkinId, skin.Slug);

    private static string? SkinFallbackImage(int championId, int skinId, bool chroma, int baseSkinId, string slug)
    {
        if (!chroma) return null;
        return SkinImage(championId, baseSkinId > 0 ? baseSkinId : skinId, false, slug);
    }
}
