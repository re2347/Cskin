using System.Text.Json.Serialization;

namespace CskinNative.Services;

public sealed class Champion
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Icon { get; set; } = "";
    public int SkinCount { get; set; }
    public List<Skin> Skins { get; set; } = [];
}

public sealed class ChampionSelection
{
    // The portable engine has returned both camelCase numeric values and
    // string values across releases. Keep the client sync tolerant of either
    // format and of the legacy snake_case field name.
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int ChampionId { get; set; }

    [JsonPropertyName("champion_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int ChampionIdSnakeCase { get; set; }

    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Id { get; set; }

    public bool Available { get; set; }
    public string Phase { get; set; } = "";
    public int SelectedSkinId { get; set; }

    [JsonIgnore]
    public int ResolvedChampionId => ChampionId > 0 ? ChampionId : ChampionIdSnakeCase > 0 ? ChampionIdSnakeCase : Id;
}

public sealed class Skin
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public int ChampionId { get; set; }
    public string ChampionName { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Image { get; set; } = "";
    public bool Chroma { get; set; }
    public string ChromaColor { get; set; } = "";
    public int BaseSkinId { get; set; }

    [JsonIgnore]
    public string? LocalizedName { get; set; }

    // Some newly published chroma IDs do not have a public CDN thumbnail yet.
    // Keep the base skin artwork as a UI-only fallback so a missing chroma
    // image never turns the inspector or picker into an empty placeholder.
    [JsonIgnore]
    public string? FallbackImage { get; set; }

    [JsonIgnore]
    public string? LocalPreviewPath { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(LocalizedName)
        ? (string.IsNullOrWhiteSpace(Name) ? $"皮肤 {Id}" : Name)
        : LocalizedName;
}

public sealed class Health
{
    public bool Engine { get; set; }
    public bool RoseEngine { get; set; }
    public int Skins { get; set; }
    public ApplierStatus Applier { get; set; } = new();
}

public sealed class ApplierStatus
{
    public bool ToolsReady { get; set; }
    public string Mode { get; set; } = "";
    public bool OverlayRunning { get; set; }
}

public sealed class ApplyResult
{
    public bool Ok { get; set; }
    public int SkinId { get; set; }
    public string Mode { get; set; } = "";
    public string OverlayStatus { get; set; } = "";
    // The runner is started before League may create its game process.  This
    // field distinguishes a live waiting runner from a DLL-confirmed apply.
    public string InjectionStatus { get; set; } = "";
    public int OverlayWadCount { get; set; }
    public long OverlayWadBytes { get; set; }
    public string Error { get; set; } = "";
}

public sealed class RebuildResult
{
    public int Skins { get; set; }
}
