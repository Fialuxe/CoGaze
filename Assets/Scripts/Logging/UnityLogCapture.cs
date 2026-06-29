using System;
using UnityEngine;

// Replaces Unity's log handler: Debug.Log → FileLogger only; warnings/errors → FileLogger + console.
public class UnityLogCapture : MonoBehaviour
{
    private ILogHandler _defaultHandler;

    private void Awake()
    {
        _defaultHandler = Debug.unityLogger.logHandler;
        Debug.unityLogger.logHandler = new FileOnlyLogHandler(_defaultHandler);
    }

    private void OnDestroy()
    {
        if (_defaultHandler != null)
            Debug.unityLogger.logHandler = _defaultHandler;
        FileLogger.Close();
    }

    private class FileOnlyLogHandler : ILogHandler
    {
        private readonly ILogHandler _inner;

        public FileOnlyLogHandler(ILogHandler inner) => _inner = inner;

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string msg;
            try
            {
                msg = args.Length > 0 ? string.Format(format, args) : format;
            }
            catch (FormatException)
            {
                // Malformed format string — fall back to raw format text so the
                // log entry is still recorded rather than dropped entirely.
                msg = format;
            }

            string category = logType switch
            {
                LogType.Error   => "ERROR",
                LogType.Warning => "WARN",
                _               => "INFO"
            };
            FileLogger.Log(category, msg);

            if (logType != LogType.Log)
                _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            FileLogger.Log("EXCEPTION", exception.ToString());
            _inner.LogException(exception, context);
        }
    }
}
