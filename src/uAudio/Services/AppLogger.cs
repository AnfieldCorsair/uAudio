using System;
using System.Diagnostics;
using System.IO;
using uAudio;

namespace uAudio.Services
{
    public static class AppLogger
    {
        private static readonly object SyncRoot = new object();

        private static string LogDirectoryPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppName, "Logs");

        private static string LogFilePath => Path.Combine(LogDirectoryPath, "uaudio.log");

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warning(string message, Exception? exception = null)
        {
            Write("WARN", message, exception);
        }

        public static void Error(string message, Exception? exception = null)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception? exception)
        {
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            Trace.WriteLine(line);

            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectoryPath);
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Logging must never break audio/editing paths.
            }
        }
    }
}
