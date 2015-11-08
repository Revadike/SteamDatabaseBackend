/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using SteamKit2;
using System.Diagnostics;

namespace SteamDatabaseBackend
{
    static class Bootstrapper
    {
        public static string ProductVersion;
        private static bool CleaningUp;

        public static void Main()
        {
            Console.Title = "Steam Database";

            var version = FileVersionInfo.GetVersionInfo(typeof(Steam).Assembly.Location);

            ProductVersion = version.ProductVersion;

            Log.WriteInfo("Bootstrapper", "Steam Database, built from commit: {0}", ProductVersion);
            Log.WriteInfo("Bootstrapper", "Copyright (c) 2013-2015, SteamDB. See LICENSE file for more information.");

            try
            {
                // Just create deepest folder we will use in the app
                string filesDir = Path.Combine(Application.Path, "files", ".support", "chunks");
                Directory.CreateDirectory(filesDir);

                Settings.Load();
                LocalConfig.Load();
            }
            catch (Exception e)
            {
                Log.WriteError("Settings", "{0}", e.Message);

                return;
            }

            ErrorReporter.Init(Settings.Current.BugsnagApiKey);

            if (Settings.Current.SteamKitDebug)
            {
                DebugLog.AddListener(new Log.SteamKitLogger());
                DebugLog.Enabled = true;
            }

            AppDomain.CurrentDomain.UnhandledException += OnSillyCrashHandler;

            Console.CancelKeyPress += OnCancelKey;

            Application.Init();
        }

        private static void OnCancelKey(object sender, ConsoleCancelEventArgs e)
        {
            if (CleaningUp)
            {
                Log.WriteInfo("Bootstrapper", "Forcing exit");

                Environment.Exit(0);

                return;
            }

            e.Cancel = true;

            CleaningUp = true;

            Application.Cleanup();
        }

        private static void OnSillyCrashHandler(object sender, UnhandledExceptionEventArgs args)
        {
            var parentException = args.ExceptionObject as Exception;
            var e = parentException.InnerException ?? parentException;

            Log.WriteError("Unhandled Exception", "{0}\n{1}", e.Message, e.StackTrace);

            if (args.IsTerminating)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnSillyCrashHandler;

                IRC.Instance.SendOps("I am literally about to crash, send help. ({0})", e.Message);

                Application.Cleanup();
            }
        }
    }
}
