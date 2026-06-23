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

            _writer = new StreamWriter(path, false, System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
        }
    }

    public static void Log(string category, string message)
    {
        lock (_lock)
        {
            // Silently discard if Init() has not been called yet
            if (_writer == null) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _writer.WriteLine($"{timestamp} [{category}] {message}");
        }
    }

    public static void Close()
    {
        lock (_lock)
        {
            if (_writer == null) return;
            _writer.Flush();
            _writer.Close();
            _writer = null;
        }
    }
}
