/*
 * Log.cs copied from VoiDeD's IRC bot at https://github.com/VoiDeD/steam-irc-bot/blob/master/SteamIrcBot/Utils/Log.cs
 */

using System;
using System.IO;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class Log
    {
        private static readonly string LogDirectoryPath = InitializeLogDirectory();

        private enum Category
        {
            DEBUG,
            INFO,
            WARN,
            ERROR,
            STEAMKIT,
        }

        public class SteamKitLogger : IDebugListener
        {
            public void WriteLine(string category, string msg)
            {
                Log.WriteLine(Category.STEAMKIT, category, msg);
            }
        }

        private static readonly object logLock = new object();

        private static string InitializeLogDirectory()
        {
            if (!Settings.Current.LogToFile)
            {
                return null;
            }

            string path = null;

            try
            {
                path = Path.Combine(Application.Path, "logs");
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                Settings.Current.LogToFile = false;

                WriteError(nameof(Log), $"Unable to create logs directory: {ex.Message}");
            }

            return path;
        }

        public static void WriteDebug(string component, string str) => WriteLine(Category.DEBUG, component, str);
        public static void WriteInfo(string component, string str) => WriteLine(Category.INFO, component, str);
        public static void WriteWarn(string component, string str) => WriteLine(Category.WARN, component, str);
        public static void WriteError(string component, string str) => WriteLine(Category.ERROR, component, str);

        private static void WriteLine(Category category, string component, string str)
        {
            var date = DateTime.Now;
            var logLine =  $"{date:HH:mm:ss} [{category}] {component}: {str}{Environment.NewLine}";

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
                else if (category == Category.STEAMKIT)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
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
                var logFile = Path.Combine(LogDirectoryPath, $"{date:MMMM_dd_yyyy}.log");

                lock (logLock)
                {
                    File.AppendAllText(logFile, logLine);
                }
            });
        }
    }
}
