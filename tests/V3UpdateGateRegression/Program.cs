using CskinNative.Services;

var failures = new List<string>();

Run("versions below the minimum are blocked", () =>
{
    var decision = UpdatePolicy.Evaluate("0.3.2", UpdateManifest.Default);
    Equal(UpdateGateStatus.Required, decision.Status, "old version was not blocked");
});

Run("the minimum version is allowed", () =>
{
    var decision = UpdatePolicy.Evaluate("0.4.0", UpdateManifest.Default);
    Equal(UpdateGateStatus.Allowed, decision.Status, "minimum version was blocked");
});

Run("newer versions are allowed", () =>
{
    var decision = UpdatePolicy.Evaluate("0.4.1", UpdateManifest.Default);
    Equal(UpdateGateStatus.Allowed, decision.Status, "newer version was blocked");
});

Run("invalid manifests fail open without bypassing a valid gate", () =>
{
    var decision = UpdatePolicy.Evaluate("0.3.2", new UpdateManifest { MinimumVersion = "not-a-version" });
    Equal(UpdateGateStatus.Unavailable, decision.Status, "invalid manifest was treated as a valid gate");
});

Run("distribution information is fixed and actionable", () =>
{
    Equal("0.4.0", UpdateManifest.Default.MinimumVersion, "minimum version");
    Equal("0.4.1", UpdateManifest.Default.LatestVersion, "latest version");
    True(UpdateManifest.Default.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase), "download URL is not HTTPS");
    Equal("https://wwboj.lanzoum.com/b01euq54ah", UpdateManifest.Default.DownloadUrl, "download URL");
    Equal("c1le", UpdateManifest.Default.DownloadPassword, "download password");
    Equal("712178830", UpdateManifest.Default.GroupNumber, "group number");
});

Run("update endpoints are HTTPS and project owned", () =>
{
    True(UpdateClient.PublicEndpoints.Count >= 2, "update failover endpoints are missing");
    True(UpdateClient.PublicEndpoints.All(endpoint => endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)), "an update endpoint is not HTTPS");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAILED (" + failures.Count + ")");
    foreach (var failure in failures) Console.Error.WriteLine("- " + failure);
    return 1;
}

Console.WriteLine("PASS: all V3 update gate regression tests");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + ": " + ex.Message);
    }
}

void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(message + "; expected=" + expected + ", actual=" + actual);
}

void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
