using System;
using System.IO;

public static class FileLogger
{
    private static StreamWriter _writer;
    private static readonly object _lock = new object();

    public static void Init(string path)
    {
        lock (_lock)
        {
            // Close any previously open writer to avoid resource leak on re-init
            if (_writer != null)
            {
                try { _writer.Flush(); _writer.Close(); }
                catch { /* ignore errors on old writer */ }
                _writer = null;
            }

            // Never let a logging-setup I/O failure (bad path, permission, full disk) throw into
            // the experiment. Leave _writer null on failure so Log() silently no-ops.
            try
            {
                _writer = new StreamWriter(path, false, System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex)
            {
                _writer = null;
                System.Diagnostics.Debug.WriteLine($"[FileLogger] Init failed for '{path}': {ex.Message}");
            }
        }
    }

    public static void Log(string category, string message)
    {
        lock (_lock)
        {
            // Silently discard if Init() has not been called yet
            if (_writer == null) return;
            // A write failure (full disk, writer faulted) must never propagate into the
            // experiment loop, and must never spam — swallow silently. Logging is best-effort.
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                _writer.WriteLine($"{timestamp} [{category}] {message}");
            }
            catch { /* never let logging crash the caller */ }
        }
    }

    public static void Close()
    {
        lock (_lock)
        {
            if (_writer == null) return;
            // Flush/Close can also throw (e.g. disk full on the final flush); guard so shutdown
            // logging never throws into the caller.
            try { _writer.Flush(); _writer.Close(); }
            catch { /* never let logging crash the caller */ }
            _writer = null;
        }
    }
}
