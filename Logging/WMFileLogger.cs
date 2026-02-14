#nullable enable
using System;
using System.IO;
using System.Text;
using BepInEx;

namespace WarehouseManager.Logging
{
    internal static class WMFileLogger
    {
        private static readonly object _lock = new();
        private static StreamWriter? _writer;

        internal static bool DebugEnabled { get; private set; }
        internal static bool VerboseEnabled { get; private set; }

        private static string LogPath =>
            Path.Combine(Paths.BepInExRootPath, "patchers", "Warehouse Manager", "WarehouseManager.log");

        internal static void Init()
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                _writer?.Dispose();
                _writer = new StreamWriter(LogPath, append: true, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                Info("========================================");
                Info($"Warehouse Manager log started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Info("========================================");
            }
        }

        internal static void Shutdown()
        {
            lock (_lock)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }

        internal static void SetLevels(bool debugEnabled, bool verboseEnabled)
        {
            DebugEnabled = debugEnabled;
            VerboseEnabled = verboseEnabled;

            Info($"[LOG] Levels updated: Debug={DebugEnabled} Verbose={VerboseEnabled}");
        }

        internal static void Info(string msg) => Write("INFO", msg);
        internal static void Warn(string msg) => Write("WARN", msg);
        internal static void Error(string msg) => Write("ERR ", msg);

        internal static void Debug(string msg)
        {
            if (!DebugEnabled) return;
            Write("DBG ", msg);
        }

        internal static void Verbose(string msg)
        {
            if (!VerboseEnabled) return;
            Write("VERB", msg);
        }

        private static void Write(string level, string msg)
        {
            lock (_lock)
            {
                if (_writer == null) return;
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}");
            }
        }
    }
}
