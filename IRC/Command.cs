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

        public void Reply(string message, params object[] args)
        {
            ReplyToCommand(string.Format(message, args), false);
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
                            message = string.Format("{0}{1}{2}: {3}", Colors.LIGHTGRAY, Nickname, Colors.NORMAL, message);
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
            switch (CommandType)
            {
                case ECommandType.IRC:
                    return string.Format("IRC User \"{0}\" in \"{1}\"", SenderIdentity, Recipient);

                case ECommandType.SteamIndividual:
                    return string.Format("Steam User \"{0}\"", SenderID.Render(true));
            }

            return base.ToString();
        }
    }
}
