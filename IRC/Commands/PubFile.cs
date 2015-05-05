/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Unified.Internal;

namespace SteamDatabaseBackend
{
    class PubFileCommand : Command
    {
        private SteamUnifiedMessages.UnifiedService<IPublishedFile> PublishedFiles;

        public PubFileCommand()
        {
            Trigger = "!pubfile";
            IsSteamCommand = true;

            PublishedFiles = Steam.Instance.Client.GetHandler<SteamUnifiedMessages>().CreateService<IPublishedFile>();

            Steam.Instance.CallbackManager.Register(new Callback<SteamUnifiedMessages.ServiceMethodResponse>(OnServiceMethod));
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} !pubfile <pubfileid>", Colors.OLIVE);

                return;
            }

            ulong pubFileId;

            if (!ulong.TryParse(command.Message, out pubFileId))
            {
                CommandHandler.ReplyToCommand(command, "Invalid Published File ID");

                return;
            }

            var pubFileRequest = new CPublishedFile_GetDetails_Request
            {
                includeadditionalpreviews = true,
                includetags = true,
                includekvtags = true,
                includevotes = true,
                //includeforsaledata = true, // TODO: Needs updated steamkit
            };

            pubFileRequest.publishedfileids.Add(pubFileId);

            JobManager.AddJob(
                () => PublishedFiles.SendMessage(api => api.GetDetails(pubFileRequest)), 
                new JobManager.IRCRequest
                {
                    Command = command
                }
            );
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
                CommandHandler.ReplyToCommand(request.Command, "Unable to make service request for published file info: {0}", callback.Result);

                return;
            }

            var response = callback.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
            var details = response.publishedfiledetails.FirstOrDefault();

            if (details == null)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to make service request for published file info: the server returned no info");

                return;
            }

            var result = (EResult)details.result;

            if (result != EResult.OK)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to get published file info: {0}", result);

                return;
            }

            try
            {
                var json = JsonConvert.SerializeObject(details, Formatting.Indented);

                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ugc", string.Format("{0}.json", details.publishedfileid)), json, Encoding.UTF8);
            }
            catch (Exception e)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to save file: {0}", e.Message);

                return;
            }

            CommandHandler.ReplyToCommand(request.Command, "Title: {0}{1}{2}, Creator: {3}{4}{5}, App: {6}{7}{8}{9}, File UGC: {10}{11}{12}, Preview UGC: {13}{14}{15} -{16} {17}",
                Colors.BLUE, details.title, Colors.NORMAL,
                Colors.BLUE, new SteamID(details.creator).Render(true), Colors.NORMAL,
                Colors.BLUE, details.creator_appid,
                details.creator_appid == details.consumer_appid ? "" : string.Format(" (consumer {0})", details.consumer_appid),
                Colors.NORMAL,
                Colors.BLUE, details.hcontent_file, Colors.NORMAL,
                Colors.BLUE, details.hcontent_preview, Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetUGCURL(details.publishedfileid)
            );

            request.Command.ReplyAsNotice = true;

            CommandHandler.ReplyToCommand(request.Command, "File URL: {0} - https://steamcommunity.com/sharedfiles/filedetails/?id={1}", details.file_url, details.publishedfileid);
        }
    }
}
