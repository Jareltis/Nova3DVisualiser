namespace Nova3DVisualiser.Logging;

public static class Logger
{
    private static readonly object _lock = new();
    private static string _logPath = "";

    public static void Init(string logDirectory)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                _logPath = Path.Combine(logDirectory, $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                File.WriteAllText(_logPath, $"===== SESSION {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }
    }

    public static void Info(string message)    => Write("INFO",  message);
    public static void Warning(string message) => Write("WARN",  message);
    public static void Error(string message)   => Write("ERROR", message);
    public static void Error(string message, Exception ex)
        => Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            if (_logPath.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
            catch { /* swallow — logging must never crash the app */ }
        }
    }
}
