namespace Nova3DVisualiser.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public static class Logger
{
    private static readonly object _lock = new();
    private static string _logPath = "";
    private static LogLevel _minLevel = LogLevel.Info;   // Debug is off by default → existing output is unchanged

    // Size-based rotation: bounds total log disk to ~(KeptFiles+1) × MaxLogBytes, so a packet flood filling
    // the log can't itself become a disk-exhaustion DoS.
    public const long MaxLogBytes = 8 * 1024 * 1024;
    public const int KeptFiles = 3;
    public static bool ShouldRotate(long currentBytes) => currentBytes >= MaxLogBytes;

    // Shared rate-limiter for Anomaly() — coalesces a flood of one attack category into one WARN stream.
    private static readonly LogRateLimiter _anomalyLimiter = new();

    public static void Init(string logDirectory)
    {
        lock (_lock)
        {
            try
            {
                // Optional: honor NOVA_LOG_LEVEL (Debug/Info/Warn/Error, case-insensitive).
                string? env = Environment.GetEnvironmentVariable("NOVA_LOG_LEVEL");
                if (!string.IsNullOrWhiteSpace(env) && Enum.TryParse(env, ignoreCase: true, out LogLevel lvl)) _minLevel = lvl;

                Directory.CreateDirectory(logDirectory);
                _logPath = Path.Combine(logDirectory, $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                File.WriteAllText(_logPath, $"===== SESSION {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }
    }

    public static void SetMinLevel(LogLevel level) { lock (_lock) { _minLevel = level; } }

    public static void Debug(string message)   => Write(LogLevel.Debug, "DEBUG", message);
    public static void Info(string message)    => Write(LogLevel.Info,  "INFO",  message);
    public static void Warning(string message) => Write(LogLevel.Warn,  "WARN",  message);
    public static void Error(string message)   => Write(LogLevel.Error, "ERROR", message);
    public static void Error(string message, Exception ex)
        => Write(LogLevel.Error, "ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    // Rate-limited anomaly logging: `key` is a COARSE category (few distinct values — e.g. "frame-oversize");
    // the variable detail (endpoint, filename) goes in `message`, NOT the key, so one attack type collapses
    // to one WARN stream. Duplicates within the cooldown are dropped; the next emit appends "(+N suppressed)".
    public static void Anomaly(string key, string message)
    {
        if (!_anomalyLimiter.ShouldLog(key, Environment.TickCount64, out int suppressed)) return;
        Write(LogLevel.Warn, "WARN", suppressed > 0 ? $"{message} (+{suppressed} suppressed)" : message);
    }

    private static void Write(LogLevel level, string levelTag, string message)
    {
        if (level < _minLevel) return;   // level filter (coarse enum read; SetMinLevel is rare)
        lock (_lock)
        {
            if (_logPath.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                RotateIfNeeded();
                File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{levelTag}] {message}{Environment.NewLine}");
            }
            catch { /* swallow — logging must never crash the app */ }
        }
    }

    // Caller holds _lock. Never throws. If the live log is at/over the cap: delete <log>.KeptFiles, shift
    // <log>.{i} → <log>.{i+1} (i = KeptFiles-1 … 1), rename <log> → <log>.1; the next append recreates <log>.
    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logPath);
            if (!fi.Exists || !ShouldRotate(fi.Length)) return;

            string Slot(int i) => $"{_logPath}.{i}";
            try { if (File.Exists(Slot(KeptFiles))) File.Delete(Slot(KeptFiles)); } catch { }
            for (int i = KeptFiles - 1; i >= 1; i--)
                try { if (File.Exists(Slot(i))) File.Move(Slot(i), Slot(i + 1), overwrite: true); } catch { }
            try { File.Move(_logPath, Slot(1), overwrite: true); } catch { }
        }
        catch { /* rotation must never crash logging */ }
    }
}
