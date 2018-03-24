/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class EnumCommand : Command
    {
        readonly IEnumerable<Type> SteamKitEnums;

        public EnumCommand()
        {
            Trigger = "enum";

            SteamKitEnums = typeof(CMClient).Assembly.GetTypes()
                .Where(x => x.IsEnum && x.Namespace.StartsWith("SteamKit2", StringComparison.Ordinal))
                // some inner namespaces have enums that have matching names, but we (most likely) want to match against the root enums
                // so we order by having the root enums first
                .OrderByDescending(x => x.Namespace == "SteamKit2");
        }

        public override async Task OnCommand(CommandArguments command)
        {
            await Task.Yield();

            if (command.Message.Equals("list", StringComparison.CurrentCultureIgnoreCase))
            {
                command.Reply(string.Join(", ", SteamKitEnums.Select(@enum => @enum.Name)));

                return;
            }

            if (command.Message.Length == 0)
            {
                command.Reply("Usage:{0} enum <enumname> [value or substring [deprecated]]", Colors.OLIVE);

                return;
            }

            var args = command.Message.Split(' ');
            var enumType = args[0].Replace("SteamKit2.", "");

            var matchingEnumType = SteamKitEnums
                .FirstOrDefault(x => x.Name.Equals(enumType, StringComparison.OrdinalIgnoreCase) || GetDottedTypeName(x).IndexOf(enumType, StringComparison.OrdinalIgnoreCase) != -1);

            if (matchingEnumType == null)
            {
                command.Reply("No such enum type.");

                return;
            }

            bool includeDeprecated = args.Length > 2 && args[2].Equals("deprecated", StringComparison.OrdinalIgnoreCase);

            GetType().GetMethod("RunForEnum", BindingFlags.Instance | BindingFlags.NonPublic)
                .MakeGenericMethod(matchingEnumType)
                .Invoke(this, new object[] { args.Length > 1 ? args[1] : string.Empty, command, includeDeprecated });
        }

        void RunForEnum<TEnum>(string inputValue, CommandArguments command, bool includeDeprecated)
            where TEnum : struct
        {
            var enumName = GetDottedTypeName(typeof(TEnum));

            if (Enum.TryParse(inputValue, out TEnum enumValue))
            {
                command.Reply("{0}{1}{2} ({3}) ={4} {5}", Colors.LIGHTGRAY, enumName, Colors.NORMAL, Enum.Format(typeof(TEnum), enumValue, "D"), Colors.BLUE, ExpandEnumFlagsToString(enumValue));

                return;
            }
                
            var enumValues = Enum.GetValues(typeof(TEnum)).Cast<TEnum>();

            if (!includeDeprecated)
            {
                enumValues = enumValues.Except(enumValues.Where(x => typeof(TEnum).GetMember(x.ToString())[0].GetCustomAttributes(typeof(ObsoleteAttribute), false).Any()));
            }

            if (!string.IsNullOrEmpty(inputValue))
            {
                enumValues = enumValues.Where(x => x.ToString().IndexOf(inputValue, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var count = enumValues.Count();

            if (count == 0)
            {
                command.Reply("No matches found.");

                return;
            }

            if (count > 10)
            {
                if (!string.IsNullOrEmpty(inputValue))
                {
                    command.Reply("More than 10 results found.");

                    return;
                }

                enumValues = enumValues.Take(10);
            }

            var formatted = string.Join(", ", enumValues.Select(@enum => string.Format("{0}{1}{2} ({3})", Colors.BLUE, @enum.ToString(), Colors.NORMAL, Enum.Format(typeof(TEnum), @enum, "D"))));

            if (count > 10)
            {
                formatted = string.Format("{0}, and {1} more...", formatted, count - 10);
            }

            command.Reply("{0}{1}{2}: {3}", Colors.LIGHTGRAY, enumName, Colors.NORMAL, formatted);
        }

        private static string ExpandEnumFlagsToString<TEnum>(TEnum enumValue)
        {
            if (typeof(TEnum).GetCustomAttributes<FlagsAttribute>().Any())
            {
                var definedFlags = new List<string>();
                ulong flags = Convert.ToUInt64(enumValue);
                ulong i = 0;
                int currentFlag = -1;

                while (i < flags)
                {
                    var flag = (1UL << ++currentFlag);

                    i += flag;

                    if ((flag & flags) == 0)
                    {
                        continue;
                    }

                    var flagObject = Enum.ToObject(typeof(TEnum), flag);

                    if (Enum.IsDefined(typeof(TEnum), flagObject))
                    {
                        definedFlags.Add(Enum.ToObject(typeof(TEnum), flagObject).ToString());
                    }
                    else
                    {
                        definedFlags.Add(string.Format("{0}(1<<{1}){2}", Colors.RED, currentFlag, Colors.BLUE));
                    }
                }

                // TODO: Handle odd flags that aren't (1<<x)

                if (definedFlags.Any())
                {
                    return string.Join(", ", definedFlags);
                }
            }

            return enumValue.ToString();
        }

        private static string GetDottedTypeName(Type type)
        {
            // @VoiDeD:
            // naive implementation of programmer friendly type full names
            // ideally we'd want something like http://stackoverflow.com/a/28943180/139147
            // but bringing in codedom is probably like using a sledgehammer to open a sliding glass door

            return type.FullName?
                .Replace('+', '.')
                .Replace("SteamKit2.", "");
        }
    }
}
