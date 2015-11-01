/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Threading.Tasks;

namespace SteamDatabaseBackend
{
    static class TaskManager
    {
        public static Task Run(Func<Task> function)
        {
            var t = Task.Run(function);

            RegisterErrorHandler(t);

            return t;
        }

        public static Task Run(Action action)
        {
            var t = Task.Run(action);
                
            RegisterErrorHandler(t);

            return t;
        }

        public static void RegisterErrorHandler(Task t)
        {
            t.ContinueWith(task =>
            {
                ErrorReporter.Notify(task.Exception);

                task.Exception.Flatten().Handle(e =>
                {
                    Log.WriteError("Task Manager", "Exception: {0}\n{1}", e.Message, e.StackTrace);

                    return false;
                });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
