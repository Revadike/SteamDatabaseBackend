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

        public bool IsAdminCommand { get; protected set; }
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
        public bool IsUserAdmin { get; set; }

        public void Notice(string message, params object[] args)
        {
            ReplyToCommand(string.Format(message, args), true);
        }

        public void Notice(string message)
        {
            ReplyToCommand(message, true);
        }

        public void Reply(string message, params object[] args)
        {
            ReplyToCommand(string.Format(message, args), false);
        }

        public void Reply(string message)
        {
            ReplyToCommand(message, false);
        }

        private void ReplyToCommand(string message, bool notice)
        {
            switch (CommandType)
            {
                case ECommandType.IRC:
                    var isChannelMessage = IRC.IsRecipientChannel(Recipient);
                    var recipient = Recipient;

                    if (isChannelMessage)
                    {
                        if (!notice)
                        {
                            message = $"{Nickname}: {message}";
                        }
                        else
                        {
                            recipient = SenderIdentity.Nickname.ToString();
                        }
                    }
                    else
                    {
                        recipient = SenderIdentity.Nickname.ToString();
                    }

                    IRC.Instance.SendReply(recipient, message, notice);

                    break;

                case ECommandType.SteamIndividual:
                    if (!Steam.Instance.Client.IsConnected)
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
                ECommandType.IRC => string.Format("IRC User \"{0}\" in \"{1}\"", SenderIdentity, Recipient),
                ECommandType.SteamIndividual => string.Format("Steam User \"{0}\"", SenderID.Render(true)),
                _ => base.ToString(),
            };
        }
    }
}
