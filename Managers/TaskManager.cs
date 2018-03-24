/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
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
        public static CancellationTokenSource TaskCancellationToken { get; } = new CancellationTokenSource();

        public static int TasksCount => Tasks.Count;

        public static Task<T> Run<T>(Func<T> function)
        {
            var t = Task.Run(function, TaskCancellationToken.Token);

            AddTask(t);
            RegisterErrorHandler(t);

            return t;
        }

        public static Task RunAsync(Action action)
        {
            var t = Task.Run(action, TaskCancellationToken.Token);

            AddTask(t);
            RegisterErrorHandler(t);

            return t;
        }

        private static void AddTask(Task t)
        {
            Tasks.TryAdd(t, 1);

            t.ContinueWith(task =>
            {
                Tasks.TryRemove(task, out _);
            });
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

        public static void CancelAllTasks()
        {
            TaskCancellationToken.Cancel();
        }
    }
}
