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

                WriteError("Unable to create logs directory: {0}", ex.Message);
            }

            return path;
        }

        public static void WriteDebug(string component, string format, params object[] args) => WriteLine(Category.DEBUG, component, format, args);
        public static void WriteInfo(string component, string format, params object[] args) => WriteLine(Category.INFO, component, format, args);
        public static void WriteWarn(string component, string format, params object[] args) => WriteLine(Category.WARN, component, format, args);
        public static void WriteError(string component, string format, params object[] args) => WriteLine(Category.ERROR, component, format, args);

        private static void WriteLine(Category category, string component, string format, params object[] args)
        {
            var date = DateTime.Now;
            var logLine =  $"{DateTime.Now:HH:mm:ss} [{category}] {component}: {(args.Length > 0 ? string.Format(format, args) : format)}{Environment.NewLine}";

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
