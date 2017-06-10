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
            AppDomain.CurrentDomain.UnhandledException += OnSillyCrashHandler;

            Console.Title = "Steam Database";

            var version = FileVersionInfo.GetVersionInfo(typeof(Steam).Assembly.Location);

            ProductVersion = version.ProductVersion;

            // Load settings file before logging as it can be enabled in settings
            Settings.Load();

            Log.WriteInfo("Bootstrapper", "Steam Database, built from commit: {0}", ProductVersion);
            Log.WriteInfo("Bootstrapper", "Copyright (c) 2013-2015, SteamDB. See LICENSE file for more information.");

            // Just create deepest folder we will use in the app
            var filesDir = Path.Combine(Application.Path, "files", ".support", "chunks");
            Directory.CreateDirectory(filesDir);

            Settings.Initialize();
            LocalConfig.Load();

            if (Settings.Current.SteamKitDebug)
            {
                DebugLog.AddListener(new Log.SteamKitLogger());
                DebugLog.Enabled = true;
            }

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

            Log.WriteError("Unhandled Exception", "{0}{1}{2}", e.Message, Environment.NewLine, e.StackTrace);

            if (args.IsTerminating)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnSillyCrashHandler;

                IRC.Instance.SendOps("I am literally about to crash, send help. ({0})", e.Message);

                Application.Cleanup();
            }
        }
    }
}
