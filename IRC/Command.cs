/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using NetIrc2;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal enum ECommandType
    {
        IRC,
        SteamIndividual
    }

    internal abstract class Command
    {
        public string Trigger { get; protected set; }
        public bool IsSteamCommand { get; protected set; }
        public abstract Task OnCommand(CommandArguments command);
    }

    internal class CommandArguments
    {
        public string Message { get; set; }
        public string Recipient { get; set; }
        public string Nickname { get; set; }
        public IrcIdentity SenderIdentity { get; set; }
        public ECommandType CommandType { get; set; }
        public SteamID SenderID { get; set; }
        
        public void Reply(string message)
        {
            switch (CommandType)
            {
                case ECommandType.IRC:
                    var isChannelMessage = IRC.IsRecipientChannel(Recipient);
                    var recipient = Recipient;

                    if (isChannelMessage)
                    {
                        message = $"{Nickname}: {message}";
                    }
                    else
                    {
                        recipient = SenderIdentity.Nickname.ToString();
                    }

                    IRC.Instance.SendReply(recipient, message);

                    break;

                case ECommandType.SteamIndividual:
                    if (!Steam.Instance.IsLoggedOn)
                    {
                        break;
                    }

                    Steam.Instance.Friends.SendChatMessage(
                        SenderID,
                        EChatEntryType.ChatMsg,
                        Colors.StripColors(message)
                    );

                    break;
            }
        }

        public override string ToString()
        {
            return CommandType switch
            {
                ECommandType.IRC => $"IRC User \"{SenderIdentity}\" in \"{Recipient}\"",
                ECommandType.SteamIndividual => $"Steam User \"{SenderID.Render()}\"",
                _ => base.ToString(),
            };
        }
    }
}
