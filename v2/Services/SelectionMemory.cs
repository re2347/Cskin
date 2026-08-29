using System.Text.Json;

namespace CskinNative.Services;

public sealed class SelectionMemory
{
    private const int CurrentSchema = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(AppPaths.Root, "settings.json");

    public int Schema { get; set; } = CurrentSchema;
    public int LastChampionId { get; set; }
    public Dictionary<int, int> LastSkinByChampion { get; set; } = [];

    public static SelectionMemory Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var value = JsonSerializer.Deserialize<SelectionMemory>(File.ReadAllText(FilePath), JsonOptions);
                return value is { Schema: CurrentSchema } ? value : new();
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new();
    }

    public int? GetSkinId(int championId) =>
        LastSkinByChampion.TryGetValue(championId, out var skinId) && skinId > 0 ? skinId : null;

    public void Remember(int championId, int skinId)
    {
        if (championId <= 0 || skinId <= 0) return;
        LastChampionId = championId;
        LastSkinByChampion[championId] = skinId;
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var partial = FilePath + ".partial";
            File.WriteAllText(partial, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(partial, FilePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
