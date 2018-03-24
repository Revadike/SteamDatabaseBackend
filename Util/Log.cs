/*
 * Log.cs copied from VoiDeD's IRC bot at https://github.com/VoiDeD/steam-irc-bot/blob/master/SteamIrcBot/Utils/Log.cs
 */

using System;
using System.IO;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class Log
    {
        private static readonly string LogDirectoryPath;

        private enum Category
        {
            DEBUG,
            INFO,
            WARN,
            ERROR
        }

        public class SteamKitLogger : IDebugListener
        {
            public void WriteLine(string category, string msg)
            {
                WriteDebug(category, msg);
            }
        }

        private static readonly object logLock = new object();

        static Log()
        {
            if (!Settings.Current.LogToFile)
            {
                return;
            }

            try
            {
                LogDirectoryPath = Path.Combine(Application.Path, "logs");
                Directory.CreateDirectory(LogDirectoryPath);
            }
            catch (Exception ex)
            {
                Settings.Current.LogToFile = false;

                WriteError("Unable to create logs directory: {0}", ex.Message);
            }
        }

        public static void WriteDebug(string component, string format, params object[] args) => WriteLine(Category.DEBUG, component, format, args);
        public static void WriteInfo(string component, string format, params object[] args) => WriteLine(Category.INFO, component, format, args);
        public static void WriteWarn(string component, string format, params object[] args) => WriteLine(Category.WARN, component, format, args);
        public static void WriteError(string component, string format, params object[] args) => WriteLine(Category.ERROR, component, format, args);

        private static void WriteLine(Category category, string component, string format, params object[] args)
        {
            var date = DateTime.Now;
            var logLine = string.Format(
                "{0} [{1}] {2}: {3}{4}",
                DateTime.Now.ToString("HH:mm:ss"),
                category,
                component,
                string.Format(format, args),
                Environment.NewLine
            );

            lock (logLock)
            {
                if (category == Category.ERROR)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write(logLine);
                    Console.ResetColor();
                }
                else if (category == Category.DEBUG)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write(logLine);
                    Console.ResetColor();
                }
                else if (category == Category.WARN)
                {
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.Write(logLine);
                    Console.ResetColor();
                }
                else
                {
                    Console.Write(logLine);
                }
            }

            if (!Settings.Current.LogToFile)
            {
                return;
            }

            Task.Run(() =>
            {
                var logFile = Path.Combine(LogDirectoryPath, string.Format("{0}.log", date.ToString("MMMM_dd_yyyy")));

                lock (logLock)
                {
                    File.AppendAllText(logFile, logLine);
                }
            });
        }
    }
}
