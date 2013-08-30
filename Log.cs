/*
 * Log.cs copied from VoiDeD's IRC bot at https://github.com/VoiDeD/steam-irc-bot/blob/master/SteamIrcBot/Utils/Log.cs
 */
using System;
using System.IO;
using SteamKit2;

namespace PICSUpdater
{
    class SteamKitLogger : IDebugListener
    {
        public void WriteLine( string category, string msg )
        {
            Log.WriteDebug( category, msg );
        }
    }

    static class Log
    {
        const string LOG_DIRECTORY = "logs";

        public enum Category
        {
            Debug,
            Info,
            Warn,
            Error,
        }

        static object logLock = new object();

        static Log()
        {
            try
            {
                string logsDir = Path.Combine( AppDomain.CurrentDomain.BaseDirectory, LOG_DIRECTORY );
                Directory.CreateDirectory( logsDir );
            }
            catch ( Exception ex )
            {
                Console.WriteLine( "ERROR: Unable to create logs directory: {0}", ex.Message );
                return;
            }
        }

        public static void WriteDebug( string component, string format, params object[] args )
        {
            WriteLine( Category.Debug, component, format, args );
        }

        public static void WriteInfo( string component, string format, params object[] args )
        {
            WriteLine( Category.Info, component, format, args );
        }

        public static void WriteWarn( string component, string format, params object[] args )
        {
            WriteLine( Category.Warn, component, format, args );
        }

        public static void WriteError( string component, string format, params object[] args )
        {
            WriteLine( Category.Error, component, format, args );
        }

        public static void WriteLine( Category category, string component, string format, params object[] args )
        {
//#if !DEBUG
//            if ( category == Category.Debug )
//                return; // don't print debug messages in release builds
//#endif

            string logLine = string.Format(
                "{0} [{1}] {2}: {3}{4}",
                DateTime.Now.ToLongTimeString(),
                category.ToString().ToUpper(),
                component,
                string.Format( format, args ),
                Environment.NewLine
            );

            Console.Write( logLine );

            try
            {
                lock ( logLock )
                {
                    File.AppendAllText( GetLogFile(), logLine );
                }
            }
            catch ( Exception ex )
            {
                Console.WriteLine( "Unable to log to file: {0}", ex.Message );
            }
        }

        static string GetLogFile()
        {
            DateTime dateNow = DateTime.Now;

            string month = dateNow.ToString( "MMMM" );
            string day = dateNow.ToString( "dd" );
            string year = dateNow.ToString( "yyyy" );

            string logFile = string.Format( "{0}_{1}_{2}.log", month, day, year );

            return Path.Combine( AppDomain.CurrentDomain.BaseDirectory, LOG_DIRECTORY, logFile );
        }
    }
}
