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
        public static Task<T> Run<T>(Func<T> function, TaskCreationOptions options)
        {
            var t = new Task<T>(function, options);

            RegisterErrorHandler(t);

            t.Start();

            return t;
        }

        public static Task<T> Run<T>(Func<T> function)
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
                task.Exception.Flatten().Handle(e =>
                {
                    ErrorReporter.Notify("Task Manager", e);

                    return false;
                });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
