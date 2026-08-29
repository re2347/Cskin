using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CskinNative.Services;

public sealed class LeaseStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CskinNative.Lease.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AuthorizationLease? Load()
    {
        try
        {
            AppPaths.ImportLegacyAuthorization();
            if (!File.Exists(AppPaths.LeaseFile)) return null;
            var encrypted = File.ReadAllBytes(AppPaths.LeaseFile);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AuthorizationLease>(json, JsonOptions);
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public bool Save(AuthorizationLease lease)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AuthorizationRoot);
            var json = JsonSerializer.SerializeToUtf8Bytes(lease, JsonOptions);
            var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            var partial = AppPaths.LeaseFile + ".partial";
            File.WriteAllBytes(partial, encrypted);
            File.Move(partial, AppPaths.LeaseFile, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.LeaseFile)) File.Delete(AppPaths.LeaseFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class RememberedLicenseStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CskinNative.RememberedLicense.v1");

    public string? Load()
    {
        try
        {
            AppPaths.ImportLegacyAuthorization();
            if (!File.Exists(AppPaths.RememberedCodeFile)) return null;
            var encrypted = File.ReadAllBytes(AppPaths.RememberedCodeFile);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(bytes).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public bool Save(string code)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AuthorizationRoot);
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(code.Trim()), Entropy, DataProtectionScope.CurrentUser);
            var partial = AppPaths.RememberedCodeFile + ".partial";
            File.WriteAllBytes(partial, encrypted);
            File.Move(partial, AppPaths.RememberedCodeFile, true);
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.RememberedCodeFile)) File.Delete(AppPaths.RememberedCodeFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class AuthorizationLease
{
    public string LicenseId { get; set; } = "";
    public int PlanDays { get; set; }
    public string DeviceId { get; set; } = "";
    public string LeaseId { get; set; } = "";
    public string LeaseToken { get; set; } = "";
    public long ServerTime { get; set; }
    public long ExpiresAt { get; set; }
    public long LeaseExpiresAt { get; set; }
    public long GraceUntil { get; set; }
    public long SavedAtUtc { get; set; }
}
