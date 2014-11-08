/*
 * Log.cs copied from VoiDeD's IRC bot at https://github.com/VoiDeD/steam-irc-bot/blob/master/SteamIrcBot/Utils/Log.cs
 */
using System;
using System.IO;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class Log
    {
        private const string LOG_DIRECTORY = "logs";

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
                Log.WriteDebug(category, msg);
            }
        }

        private static object logLock = new object();

        static Log()
        {
            if (!Settings.Current.LogToFile)
            {
                return;
            }

            try
            {
                string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LOG_DIRECTORY);
                Directory.CreateDirectory(logsDir);
            }
            catch (Exception ex)
            {
                Settings.Current.LogToFile = false;

                Console.WriteLine("ERROR: Unable to create logs directory: {0}", ex.Message);
            }
        }

        public static void WriteDebug(string component, string format, params object[] args)
        {
            WriteLine(Category.DEBUG, component, format, args);
        }

        public static void WriteInfo(string component, string format, params object[] args)
        {
            WriteLine(Category.INFO, component, format, args);
        }

        public static void WriteWarn(string component, string format, params object[] args)
        {
            WriteLine(Category.WARN, component, format, args);
        }

        public static void WriteError(string component, string format, params object[] args)
        {
            WriteLine(Category.ERROR, component, format, args);

            if (!component.Equals("IRC"))
            {
                IRC.Instance.SendOps("{0}ERROR in {1}:{2} {3}", Colors.RED, component, Colors.NORMAL, string.Format(format.Replace("\n", " "), args));
            }
        }

        private static void WriteLine(Category category, string component, string format, params object[] args)
        {
            string logLine = string.Format(
                "{0} [{1}] {2}: {3}{4}",
                DateTime.Now.ToLongTimeString(),
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
                else
                {
                    Console.Write(logLine);
                }
            }

            if (!Settings.Current.LogToFile)
            {
                return;
            }

            try
            {
                lock (logLock)
                {
                    File.AppendAllText(GetLogFile(), logLine);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unable to log to file: {0}", ex.Message);
            }
        }

        private static string GetLogFile()
        {
            string logFile = string.Format("{0}.log", DateTime.Now.ToString("MMMM_dd_yyyy"));

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LOG_DIRECTORY, logFile);
        }
    }
}
