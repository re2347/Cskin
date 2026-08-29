using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Management;

namespace CskinNative.Services;

public sealed class DeviceIdentity : IDisposable
{
    private const string EntropyText = "CskinNative.DeviceIdentity.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ECDsa _key;

    private DeviceIdentity(ECDsa key, string publicKeySpki, string deviceId, string legacyDeviceId)
    {
        _key = key;
        PublicKeySpki = publicKeySpki;
        DeviceId = deviceId;
        LegacyDeviceId = legacyDeviceId;
    }

    public string DeviceId { get; }
    // DeviceId is motherboard-bound for new activations. This legacy value
    // lets existing user-key-bound leases survive the migration once.
    public string LegacyDeviceId { get; }
    public string PublicKeySpki { get; }

    public static DeviceIdentity LoadOrCreate()
    {
        AppPaths.ImportLegacyAuthorization();
        try
        {
            if (File.Exists(AppPaths.DeviceIdentityFile))
            {
                var stored = JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(AppPaths.DeviceIdentityFile), JsonOptions);
                if (stored is not null && !string.IsNullOrWhiteSpace(stored.PublicKeySpki) && !string.IsNullOrWhiteSpace(stored.ProtectedPrivateKey))
                {
                    var protectedBytes = Convert.FromBase64String(stored.ProtectedPrivateKey);
                    var privateBytes = ProtectedData.Unprotect(protectedBytes, Entropy(), DataProtectionScope.CurrentUser);
                    var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(privateBytes, out _);
                    var legacyDeviceId = Hash(Encoding.UTF8.GetBytes(stored.PublicKeySpki));
                    return new DeviceIdentity(key, stored.PublicKeySpki, ResolveDeviceId(legacyDeviceId), legacyDeviceId);
                }
            }
        }
        catch (CryptographicException) { }
        catch (FormatException) { }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = created.ExportPkcs8PrivateKey();
        var publicKey = created.ExportSubjectPublicKeyInfo();
        var storedIdentity = new StoredIdentity(
            Convert.ToBase64String(publicKey),
            Convert.ToBase64String(ProtectedData.Protect(privateKey, Entropy(), DataProtectionScope.CurrentUser)));
        Save(storedIdentity);
        var legacyId = Hash(Encoding.UTF8.GetBytes(storedIdentity.PublicKeySpki));
        return new DeviceIdentity(created, storedIdentity.PublicKeySpki, ResolveDeviceId(legacyId), legacyId);
    }

    public string Sign(string value)
    {
        // WebCrypto uses the IEEE P1363 r||s representation for ECDSA. Keep
        // the format explicit so the Worker can verify the same bytes.
        var signature = _key.SignData(
            Encoding.UTF8.GetBytes(value),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    public void Dispose() => _key.Dispose();

    private static void Save(StoredIdentity identity)
    {
        Directory.CreateDirectory(AppPaths.AuthorizationRoot);
        var partial = AppPaths.DeviceIdentityFile + ".partial";
        File.WriteAllText(partial, JsonSerializer.Serialize(identity, JsonOptions), Encoding.UTF8);
        File.Move(partial, AppPaths.DeviceIdentityFile, true);
    }

    private static byte[] Entropy() => Encoding.UTF8.GetBytes(EntropyText);

    private static string ResolveDeviceId(string legacyDeviceId)
    {
        var board = HardwareFingerprint.Read();
        if (string.IsNullOrWhiteSpace(board)) return legacyDeviceId;
        return Hash(Encoding.UTF8.GetBytes($"CskinNative.Motherboard.v1\n{board}"));
    }

    private static string Hash(ReadOnlySpan<byte> value)
    {
        var digest = SHA256.HashData(value);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private sealed record StoredIdentity(string PublicKeySpki, string ProtectedPrivateKey);
}

internal static class HardwareFingerprint
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "UNKNOWN", "DEFAULT STRING", "TO BE FILLED BY O.E.M.", "TO BE FILLED BY OEM", "NONE", "N/A", "NA"
    };

    public static string? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer,Product,SerialNumber,Version FROM Win32_BaseBoard");
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);
            foreach (ManagementObject board in searcher.Get())
            {
                var values = new[]
                {
                    Clean(board["Manufacturer"]?.ToString()),
                    Clean(board["Product"]?.ToString()),
                    Clean(board["SerialNumber"]?.ToString()),
                    Clean(board["Version"]?.ToString())
                }.Where(value => value is not null).ToArray();
                if (values.Length >= 2 && values.Any(value => value!.Length >= 4))
                    return string.Join("|", values);
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException) { }

        // Some firmware does not expose Win32_BaseBoard serial data. The
        // system-product UUID is still stable across reinstall and is a safer
        // fallback than a mutable user profile or network adapter address.
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);
            foreach (ManagementObject product in searcher.Get())
            {
                var uuid = Clean(product["UUID"]?.ToString());
                if (uuid is not null && uuid.Length >= 8) return $"SYSTEM-UUID|{uuid}";
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    private static string? Clean(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) || PlaceholderValues.Contains(normalized)
            ? null
            : normalized;
    }
}
