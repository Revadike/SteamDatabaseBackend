/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class EnumCommand : Command
    {
        IEnumerable<Type> SteamKitEnums;

        public EnumCommand()
        {
            Trigger = "!enum";

            SteamKitEnums = typeof(CMClient).Assembly.GetTypes()
                .Where(x => x.IsEnum)
                .Where(x => x.Namespace == "SteamKit2");
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Equals("list", StringComparison.CurrentCultureIgnoreCase))
            {
                CommandHandler.ReplyToCommand(command, string.Join(", ", SteamKitEnums.Select(@enum => @enum.Name)));

                return;
            }

            var args = command.Message.Split(' ');

            if (args.Length < 2)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} !enum <enumname> <value or substring> [deprecated]", Colors.OLIVE);

                return;
            }

            var matchingEnumType = SteamKitEnums.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.InvariantCultureIgnoreCase));

            if (matchingEnumType == null)
            {
                CommandHandler.ReplyToCommand(command, "No such enum type.");

                return;
            }

            bool includeDeprecated = args.Length > 2 && args[2].Equals("deprecated", StringComparison.InvariantCultureIgnoreCase);

            GetType().GetMethod("RunForEnum", BindingFlags.Instance | BindingFlags.NonPublic)
                .MakeGenericMethod(matchingEnumType)
                .Invoke(this, new object[] { args[1], command, includeDeprecated });
        }

        void RunForEnum<TEnum>(string inputValue, CommandArguments command, bool includeDeprecated)
            where TEnum : struct
        {
            TEnum enumValue;

            if (Enum.TryParse(inputValue, out enumValue))
            {
                CommandHandler.ReplyToCommand(command, "{0}{1}{2} = {3}", Colors.LIGHTGRAY, Enum.Format(typeof(TEnum), enumValue, "D"), Colors.NORMAL, enumValue);

                return;
            }
                
            var enumValues = Enum.GetValues(typeof(TEnum)).Cast<TEnum>();

            if (!includeDeprecated)
            {
                enumValues = enumValues.Except(enumValues.Where(x => typeof(TEnum).GetMember(x.ToString())[0].GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).Any()));
            }

            var enumValuesWithMatchingName = enumValues.Where(x => x.ToString().IndexOf(inputValue, StringComparison.InvariantCultureIgnoreCase) >= 0);
            var count = enumValuesWithMatchingName.Count();

            if (count == 0)
            {
                CommandHandler.ReplyToCommand(command, "No matches found.");
            }
            else if (count > 10)
            {
                CommandHandler.ReplyToCommand(command, "More than 10 results found.");
            }
            else
            {
                var formatted = string.Join(", ", enumValuesWithMatchingName.Select(@enum => string.Format("{0} ({1})", @enum.ToString(), Enum.Format(typeof(TEnum), @enum, "D"))));
                CommandHandler.ReplyToCommand(command, formatted);
            }
        }
    }
}
