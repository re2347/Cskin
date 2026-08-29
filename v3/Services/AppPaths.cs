namespace CskinNative.Services;

public static class AppPaths
{
    private static readonly object AuthorizationMigrationLock = new();
    public static string Root { get; } = ResolveRoot();

    // A test-only override keeps clean-install checks independent. Normal
    // builds keep every mutable file beside the executable so the portable
    // folder can be moved to another computer as one self-contained tree.
    private static string ResolveRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CSKIN_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            try { return Path.GetFullPath(overrideRoot.Trim()); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        try { return Path.GetFullPath(AppContext.BaseDirectory); }
        catch (ArgumentException)
        {
            return Directory.GetCurrentDirectory();
        }
        catch (NotSupportedException)
        {
            return Directory.GetCurrentDirectory();
        }
    }

    public static string AssetsRoot => Path.Combine(Root, "Assets");
    public static string LogRoot => Path.Combine(Root, "logs");
    public static string ApplicationLogFile => Path.Combine(LogRoot, "application.log");
    // The engine is shipped as a one-dir tree and writes its catalog,
    // downloaded packages and overlay files below this same application root.
    public static string EngineRoot => Path.Combine(Root, "Engine");
    public static string? BundledEngineExecutable =>
        HasUsableEngine(EngineRoot)
            ? Path.Combine(EngineRoot, "Cskin.exe")
            : FindBundledFile(Path.Combine("Engine", "Cskin.exe"));
    public static string AuthorizationRoot => Path.Combine(Root, "authorization");
    public static string DeviceIdentityFile => Path.Combine(AuthorizationRoot, "device.identity");
    public static string LeaseFile => Path.Combine(AuthorizationRoot, "lease.bin");
    public static string RememberedCodeFile => Path.Combine(AuthorizationRoot, "remembered-license.bin");
    public static string AuthorizationEnabledMarker => Path.Combine(Root, "authorization.enabled");
    public static string SettingsFile => Path.Combine(Root, "app-settings.json");
    public static string Asset(string fileName) => Path.Combine(AssetsRoot, fileName);

    public static void ImportLegacyAuthorization()
    {
        lock (AuthorizationMigrationLock)
        {
            try
            {
                Directory.CreateDirectory(AuthorizationRoot);
                foreach (var sourceRoot in LegacyAuthorizationRoots())
                {
                    foreach (var fileName in new[] { "device.identity", "lease.bin", "remembered-license.bin" })
                    {
                        var source = Path.Combine(sourceRoot, fileName);
                        var destination = Path.Combine(AuthorizationRoot, fileName);
                        if (!File.Exists(destination) && File.Exists(source))
                            File.Copy(source, destination, overwrite: false);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void EnsureRuntime()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AssetsRoot);
        Directory.CreateDirectory(AuthorizationRoot);
        ImportLegacyAuthorization();
        Directory.CreateDirectory(LogRoot);
        // Keep the mutable engine layout visible even before the first skin
        // download. SkinRepository writes downloaded packages here.
        Directory.CreateDirectory(Path.Combine(EngineRoot, "skins"));

        EnsureEngineRuntime();

        foreach (var fileName in new[] { "skin_index.json", "skin_names_zh_CN.json", "champions_zh_CN.json", "cskin.ico" })
        {
            var source = FindBundledFile(Path.Combine("Assets", fileName));
            var target = Asset(fileName);
            if (source is null)
            {
                AppLog.WarnThrottled($"asset-missing-{fileName}", $"随包资源缺失：{fileName}", TimeSpan.FromMinutes(1));
                continue;
            }
            CopyIfMissing(source, target);
            if (File.Exists(source))
                CopyIfOutdated(source, Path.Combine(EngineRoot, "selector_assets", fileName));
        }
    }

    private static void EnsureEngineRuntime()
    {
        var sourceExecutable = FindBundledFile(Path.Combine("Engine", "Cskin.exe"));
        if (string.IsNullOrWhiteSpace(sourceExecutable)) return;
        var sourceRoot = Path.GetDirectoryName(sourceExecutable);
        if (string.IsNullOrWhiteSpace(sourceRoot)) return;

        try
        {
            Directory.CreateDirectory(EngineRoot);
            if (string.Equals(
                    Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(EngineRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                // The normal portable/installed layout already contains the
                // authoritative engine tree. Only test overrides need a copy.
                return;
            }
            var marker = Path.Combine(EngineRoot, ".engine-build");
            var sourceMarker = GetEngineMarker(sourceRoot);
            var targetMarker = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
            if (HasUsableEngine(EngineRoot) && string.Equals(sourceMarker, targetMarker, StringComparison.Ordinal))
                return;

            foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, source);
                var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                // Runtime data and cached skins belong to this machine and
                // must never be replaced by files from the publish tree.
                if (firstSegment.Equals("data", StringComparison.OrdinalIgnoreCase)
                    || firstSegment.Equals("skins", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals(".engine-build", StringComparison.OrdinalIgnoreCase)) continue;

                var target = Path.Combine(EngineRoot, relative);
                CopyIfOutdated(source, target);
            }

            if (HasUsableEngine(EngineRoot))
                File.WriteAllText(marker, sourceMarker);
        }
        catch (IOException ex) { AppLog.Error($"引擎目录准备失败：{EngineRoot}", ex); }
        catch (UnauthorizedAccessException ex) { AppLog.Error($"引擎目录无权限：{EngineRoot}", ex); }
    }

    private static string GetEngineMarker(string root)
    {
        long totalBytes = 0;
        long newestTicks = 0;
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSegment.Equals("data", StringComparison.OrdinalIgnoreCase)
                    || firstSegment.Equals("skins", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals(".engine-build", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                totalBytes += info.Length;
                newestTicks = Math.Max(newestTicks, info.LastWriteTimeUtc.Ticks);
                count++;
            }
        }
        catch (IOException ex) { AppLog.Error($"读取引擎目录标记失败：{root}", ex); }
        catch (UnauthorizedAccessException ex) { AppLog.Error($"读取引擎目录标记无权限：{root}", ex); }
        return $"{count}:{totalBytes}:{newestTicks}";
    }

    private static bool HasUsableEngine(string root) =>
        File.Exists(Path.Combine(root, "Cskin.exe"))
        && File.Exists(Path.Combine(root, "cskin_engine.pyc"))
        && File.Exists(Path.Combine(root, "tools", "mod-tools.exe"));

    public static string? FindBundledFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        foreach (var root in BundledRoots())
        {
            try
            {
                var candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return null;
    }

    private static IEnumerable<string> BundledRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        Add(AppContext.BaseDirectory);
        Add(Path.GetDirectoryName(Environment.ProcessPath));
        Add(Path.GetDirectoryName(typeof(AppPaths).Assembly.Location));
        return roots;
    }

    private static IEnumerable<string> LegacyAuthorizationRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CskinNative", "authorization"));
        Add(Path.Combine(AppContext.BaseDirectory, "authorization"));
        Add(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "authorization"));
        return roots;
    }

    public static string? Cleanup()
    {
        return DeleteDirectory(Root);
    }

    // Clear downloaded/runtime resources without removing bundled files or
    // authorization state. V3 no longer creates a local Git repository.
    public static string? CleanupRuntime()
    {
        var errors = new List<string>();
        foreach (var path in new[]
        {
            Path.Combine(EngineRoot, "data"),
            Path.Combine(EngineRoot, "skins")
        })
        {
            var error = DeleteDirectory(path);
            if (error is not null) errors.Add($"{path}: {error}");
        }

        return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
    }

    public static string? CleanupLogs()
    {
        // application.log is intentionally removed last. Callers must not
        // write another log entry after a successful deletion.
        return DeleteDirectory(LogRoot);
    }

    private static string? DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return null;
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return null;
            }
            catch (IOException) when (attempt < 23) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) when (attempt < 23) { Thread.Sleep(250); }
            catch (IOException ex)
            {
                AppLog.Error($"清理目录失败：{path}", ex);
                return ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Error($"清理目录无权限：{path}", ex);
                return ex.Message;
            }
        }

        return Directory.Exists(path) ? "本地资源仍被后台进程占用，请稍后重试" : null;
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(directory, FileAttributes.Normal); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            try { File.SetAttributes(root, FileAttributes.Normal); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CopyIfMissing(string? source, string target)
    {
        if (!File.Exists(source) || File.Exists(target)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        catch (IOException ex)
        {
            AppLog.WarnThrottled($"copy-missing-{target}", $"复制资源失败：{target}", TimeSpan.FromMinutes(1));
            AppLog.Error("复制资源异常", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.WarnThrottled($"copy-missing-permission-{target}", $"复制资源无权限：{target}", TimeSpan.FromMinutes(1));
            AppLog.Error("复制资源权限异常", ex);
        }
    }

    private static void CopyIfOutdated(string source, string target)
    {
        try
        {
            var sourceInfo = new FileInfo(source);
            var targetInfo = new FileInfo(target);
            if (targetInfo.Exists
                && targetInfo.Length == sourceInfo.Length
                && targetInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, sourceInfo.LastWriteTimeUtc);
        }
        catch (IOException ex)
        {
            AppLog.WarnThrottled($"copy-outdated-{target}", $"更新资源失败：{target}", TimeSpan.FromMinutes(1));
            AppLog.Error("更新资源异常", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.WarnThrottled($"copy-outdated-permission-{target}", $"更新资源无权限：{target}", TimeSpan.FromMinutes(1));
            AppLog.Error("更新资源权限异常", ex);
        }
    }
}
