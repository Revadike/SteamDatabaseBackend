/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDatabaseBackend
{
    static class TaskManager
    {
        private static readonly ConcurrentDictionary<Task, byte> Tasks = new ConcurrentDictionary<Task, byte>();
        private static readonly CancellationTokenSource TaskCancellationToken = new CancellationTokenSource();

        public static int TasksCount
        {
            get
            {
                return Tasks.Count;
            }
        }

        public static Task<T> Run<T>(Func<T> function, TaskCreationOptions options)
        {
            var t = new Task<T>(function, TaskCancellationToken.Token, options);

            RegisterErrorHandler(t);

            t.Start();

            return t;
        }

        public static Task<T> Run<T>(Func<T> function)
        {
            var t = Task.Run(function, TaskCancellationToken.Token);

            RegisterErrorHandler(t);

            return t;
        }

        public static Task Run(Action action)
        {
            var t = Task.Run(action, TaskCancellationToken.Token);
                
            RegisterErrorHandler(t);

            return t;
        }

        public static void RegisterErrorHandler(Task t)
        {
            Tasks.TryAdd(t, 1);

#if DEBUG
            Log.WriteDebug("Task Manager", "New task: {0}", t);

            t.ContinueWith(task =>
            {
                if (Tasks.TryRemove(task, out var value))
                {
                    Log.WriteDebug("Task Manager", "Removed task: {0} ({1} jobs left)", t, TasksCount);
                }
            });
#endif

            t.ContinueWith(task =>
            {
                task.Exception.Flatten().Handle(e =>
                {
                    ErrorReporter.Notify("Task Manager", e);

                    return false;
                });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public static void CancelAllTasks()
        {
            TaskCancellationToken.Cancel();
        }
    }
}
