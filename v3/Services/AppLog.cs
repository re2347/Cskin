using System.Text;

namespace CskinNative.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> LastMessages = new(StringComparer.Ordinal);

    public static string FilePath => AppPaths.ApplicationLogFile;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void InfoThrottled(string key, string message, TimeSpan interval) =>
        WriteThrottled("INFO", key, message, interval);

    public static void WarnThrottled(string key, string message, TimeSpan interval)
        => WriteThrottled("WARN", key, message, interval);

    private static void WriteThrottled(string level, string key, string message, TimeSpan interval)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (LastMessages.TryGetValue(key, out var previous) && now - previous < interval) return;
            LastMessages[key] = now;
        }
        Write(level, message, null);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var detail = exception is null ? "" : $" | {exception}";
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{detail}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never break cleanup, startup, or the poller.
        }
    }
}
