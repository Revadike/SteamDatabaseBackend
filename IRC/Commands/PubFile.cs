/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetIrc2.Events;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Unified.Internal;

namespace SteamDatabaseBackend
{
    class PubFileCommand : Command
    {
        private SteamUnifiedMessages.UnifiedService<IPublishedFile> PublishedFiles;
        private Regex SharedFileMatch;

        public PubFileCommand()
        {
            Trigger = "pubfile";
            IsSteamCommand = true;

            SharedFileMatch = new Regex(@"(?:^|/|\.)steamcommunity\.com\/sharedfiles\/filedetails\/\?id=(?<pubfileid>[0-9]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

            PublishedFiles = Steam.Instance.Client.GetHandler<SteamUnifiedMessages>().CreateService<IPublishedFile>();

            Steam.Instance.CallbackManager.Register(new Callback<SteamUnifiedMessages.ServiceMethodResponse>(OnServiceMethod));
        }

        public void OnMessage(ChatMessageEventArgs e)
        {
            if (!Steam.Instance.Client.IsConnected)
            {
                return;
            }

            var matches = SharedFileMatch.Matches(e.Message);

            foreach (Match match in matches)
            {
                var pubFileId = ulong.Parse(match.Groups["pubfileid"].Value);
                var pubFileRequest = new CPublishedFile_GetDetails_Request();

                pubFileRequest.publishedfileids.Add(pubFileId);

                JobManager.AddJob(
                    () => PublishedFiles.SendMessage(api => api.GetDetails(pubFileRequest)), 
                    new JobManager.IRCRequest
                    {
                        Type = JobManager.IRCRequestType.TYPE_SILENT,
                        Command = new CommandArguments
                        {
                            Recipient = e.Recipient
                        }
                    }
                );
            }
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} pubfile <pubfileid>", Colors.OLIVE);

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
                if (request.Type != JobManager.IRCRequestType.TYPE_SILENT)
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unable to make service request for published file info: {0}", callback.Result);
                }

                return;
            }

            var response = callback.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
            var details = response.publishedfiledetails.FirstOrDefault();

            if (request.Type == JobManager.IRCRequestType.TYPE_SILENT)
            {
                if (details == null || (EResult)details.result != EResult.OK)
                {
                    return;
                }

                IRC.Instance.SendReply(request.Command.Recipient,
                    string.Format("{0}\u2937 {1}{2} {3}{4}{5} for {6}{7}",
                        Colors.OLIVE,
                        Colors.NORMAL,
                        ((EWorkshopFileType)details.file_type),
                        Colors.BLUE,
                        string.IsNullOrWhiteSpace(details.title) ? details.filename : details.title,
                        Colors.NORMAL,
                        Colors.BLUE,
                        details.app_name
                    ),
                    false
                );

                return;
            }

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

            CommandHandler.ReplyToCommand(request.Command, "{0}, Title: {1}{2}{3}, Creator: {4}{5}{6}, App: {7}{8}{9}{10}, File UGC: {11}{12}{13}, Preview UGC: {14}{15}{16} -{17} {18}",
                (EWorkshopFileType)details.file_type,
                Colors.BLUE, string.IsNullOrWhiteSpace(details.title) ? "[no title]" : details.title, Colors.NORMAL,
                Colors.BLUE, new SteamID(details.creator).Render(true), Colors.NORMAL,
                Colors.BLUE, details.creator_appid,
                details.creator_appid == details.consumer_appid ? "" : string.Format(" (consumer {0})", details.consumer_appid),
                Colors.NORMAL,
                Colors.BLUE, details.hcontent_file, Colors.NORMAL,
                Colors.BLUE, details.hcontent_preview, Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetUGCURL(details.publishedfileid)
            );

            CommandHandler.ReplyToCommand(request.Command, true, "{0} - https://steamcommunity.com/sharedfiles/filedetails/?id={1}", details.file_url, details.publishedfileid);
        }
    }
}
