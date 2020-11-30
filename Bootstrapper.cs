/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class Bootstrapper
    {
        private static bool CleaningUp;

        public static async Task Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

            AppDomain.CurrentDomain.UnhandledException += OnSillyCrashHandler;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            HandleArguments(args);

            Console.Title = "Steam Database";

            // Load settings file before logging as it can be enabled in settings
            await Settings.Load();

            Log.WriteInfo(nameof(Bootstrapper), "Copyright (c) 2013-present, SteamDB. See LICENSE file for more information.");

            await Settings.Initialize();

            DebugLog.AddListener(new Log.SteamKitLogger());
            DebugLog.Enabled = true;

            Console.CancelKeyPress += OnCancelKey;

            await Application.Init();

            Steam.Instance.Tick();
        }

        private static void HandleArguments(IEnumerable<string> args)
        {
            foreach (var arg in args)
            {
                var value = arg.Split('=', 2);

                if (value[0] == "-f")
                {
                    var fullRunName = value.Length > 1 ? value[1] : string.Empty;

                    if (!Enum.TryParse(fullRunName, true, out FullRunState fullRunState))
                    {
                        if (fullRunName != string.Empty)
                        {
                            Log.WriteError(nameof(Bootstrapper), $"Unknown fullrun state: {fullRunName}");
                        }

                        Log.WriteInfo(nameof(Bootstrapper), $"Valid fullrun states are: {string.Join(", ", Enum.GetNames(typeof(FullRunState)))}");
                        Environment.Exit(1);
                    }

                    Settings.FullRun = fullRunState;

                    continue;
                }

                Log.WriteError(nameof(Bootstrapper), $"Unknown argument: {arg}");
                Environment.Exit(1);
            }
        }

        private static void OnCancelKey(object sender, ConsoleCancelEventArgs e)
        {
            if (CleaningUp)
            {
                Log.WriteInfo(nameof(Bootstrapper), "Forcing exit");

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
                    ErrorReporter.Notify(nameof(Bootstrapper), e);

                    return false;
                });
            }
            else
            {
                ErrorReporter.Notify(nameof(Bootstrapper), parentException);
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
            args.Exception.Flatten().Handle(e =>
            {
                ErrorReporter.Notify(nameof(Bootstrapper), e);

                return true;
            });
        }
    }
}
