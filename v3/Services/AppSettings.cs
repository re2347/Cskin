using System.Text.Json;

namespace CskinNative.Services;

public sealed class AppSettings
{
    private const int CurrentSchema = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int Schema { get; set; } = CurrentSchema;
    public string? GameDirectory { get; set; }
    public string? EngineExecutable { get; set; }

    private static string FilePath => AppPaths.SettingsFile;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
            return value is { Schema: CurrentSchema } ? value : new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var partial = FilePath + ".partial";
            File.WriteAllText(partial, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(partial, FilePath, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
