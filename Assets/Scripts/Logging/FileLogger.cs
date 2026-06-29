using System;
using System.IO;

public static class FileLogger
{
    private static StreamWriter s_writer;
    private static readonly object s_lock = new object();

    public static void Init(string path)
    {
        lock (s_lock)
        {
            // Close any previously open writer to avoid resource leak on re-init
            if (s_writer != null)
            {
                try { s_writer.Flush(); s_writer.Close(); }
                catch { /* ignore errors on old writer */ }
                s_writer = null;
            }

            // Never let a logging-setup I/O failure (bad path, permission, full disk) throw into
            // the experiment. Leave s_writer null on failure so Log() silently no-ops.
            try
            {
                s_writer = new StreamWriter(path, false, System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex)
            {
                s_writer = null;
                System.Diagnostics.Debug.WriteLine($"[FileLogger] Init failed for '{path}': {ex.Message}");
            }
        }
    }

    public static void Log(string category, string message)
    {
        lock (s_lock)
        {
            // Silently discard if Init() has not been called yet
            if (s_writer == null) return;
            // A write failure (full disk, writer faulted) must never propagate into the
            // experiment loop, and must never spam — swallow silently. Logging is best-effort.
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                s_writer.WriteLine($"{timestamp} [{category}] {message}");
            }
            catch { /* never let logging crash the caller */ }
        }
    }

    public static void Close()
    {
        lock (s_lock)
        {
            if (s_writer == null) return;
            // Flush/Close can also throw (e.g. disk full on the final flush); guard so shutdown
            // logging never throws into the caller.
            try { s_writer.Flush(); s_writer.Close(); }
            catch { /* never let logging crash the caller */ }
            s_writer = null;
        }
    }
}
