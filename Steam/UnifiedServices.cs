/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class UnifiedServices : SteamHandler
    {
        public UnifiedServices(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamUnifiedMessages.ServiceMethodResponse>(OnServiceMethod);
        }

        private void OnServiceMethod(SteamUnifiedMessages.ServiceMethodResponse callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job) || !job.IsCommand)
            {
                return;
            }

            var request = job.CommandRequest;

            if (callback.Result != EResult.OK)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to make service request: {0}", callback.Result);

                return;
            }

            // .chat about grate
            switch (request.Type)
            {
                case JobManager.IRCRequestType.TYPE_GAMESERVERS:
                    ServersCommand.OnServiceMethod(callback, request);
                    return;

                case JobManager.IRCRequestType.TYPE_PUBFILE:
                case JobManager.IRCRequestType.TYPE_PUBFILE_SILENT:
                    PubFileCommand.OnServiceMethod(callback, request);
                    return;
            }

            CommandHandler.ReplyToCommand(request.Command, "Unknown request type, I don't know what to do.");
        }
    }
}
