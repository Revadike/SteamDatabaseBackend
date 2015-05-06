/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    class PackageCommand : Command
    {
        public PackageCommand()
        {
            Trigger = "sub";
            IsSteamCommand = true;
        }

        public override void OnCommand(CommandArguments command)
        {
            var count = PICSProductInfo.ProcessedSubs.Count;

            if (count > 100)
            {
                CommandHandler.ReplyToCommand(command, "There are currently {0} packages awaiting to be processed, try again later.", count);

                return;
            }

            uint subID;

            if (command.Message.Length > 0 && uint.TryParse(command.Message, out subID))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(null, subID, false, false), new JobManager.IRCRequest
                {
                    Target = subID,
                    Type = JobManager.IRCRequestType.TYPE_SUB,
                    Command = command
                });

                return;
            }

            CommandHandler.ReplyToCommand(command, "Usage:{0} sub <subid>", Colors.OLIVE);
        }
    }
}
