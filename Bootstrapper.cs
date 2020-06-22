/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class Bootstrapper
    {
        private static bool CleaningUp;

        public static async Task Main()
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            AppDomain.CurrentDomain.UnhandledException += OnSillyCrashHandler;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            Console.Title = "Steam Database";

            // Load settings file before logging as it can be enabled in settings
            await Settings.Load();

            Log.WriteInfo("Bootstrapper", "Copyright (c) 2013-present, SteamDB. See LICENSE file for more information.");

            // Just create deepest folder we will use in the app
            var filesDir = Path.Combine(Application.Path, "files", ".support", "chunks");
            Directory.CreateDirectory(filesDir);

            await Settings.Initialize();
            await LocalConfig.Load();

            DebugLog.AddListener(new Log.SteamKitLogger());
            DebugLog.Enabled = true;

            Console.CancelKeyPress += OnCancelKey;

            await Application.Init();
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

            if (parentException is AggregateException aggregateException)
            {
                aggregateException.Flatten().Handle(e =>
                {
                    ErrorReporter.Notify("Bootstrapper", e);

                    return false;
                });
            }
            else
            {
                ErrorReporter.Notify("Bootstrapper", parentException);
            }

            if (args.IsTerminating)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnSillyCrashHandler;

                IRC.Instance.SendOps("🥀 Backend process has crashed.");

                Application.Cleanup();
            }
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            args.Exception?.Flatten().Handle(e =>
            {
                ErrorReporter.Notify("Bootstrapper", e);

                return true;
            });
        }
    }
}
