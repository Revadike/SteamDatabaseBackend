/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    internal class EnumCommand : Command
    {
        private readonly IEnumerable<Type> SteamKitEnums;

        public EnumCommand()
        {
            Trigger = "enum";

            SteamKitEnums = typeof(CMClient).Assembly.GetTypes()
                .Where(x => x.IsEnum && x.Namespace != null && x.Namespace.StartsWith("SteamKit2", StringComparison.Ordinal))
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
                command.Reply($"Usage:{Colors.OLIVE} enum <enumname> [value or substring [deprecated]]");

                return;
            }

            var args = command.Message.Split(' ');
            var enumType = args[0].Replace("SteamKit2.", "");

            if (int.TryParse(enumType, out _))
            {
                command.Reply($"Did you mean:{Colors.OLIVE} enum eresult {enumType}");

                return;
            }

            var matchingEnumType = SteamKitEnums
                .FirstOrDefault(x => x.Name.Equals(enumType, StringComparison.OrdinalIgnoreCase) || GetDottedTypeName(x).IndexOf(enumType, StringComparison.OrdinalIgnoreCase) != -1);

            if (matchingEnumType == null)
            {
                command.Reply("No such enum type.");

                return;
            }

            var input = args.Length > 1 ? args[1] : string.Empty;
            var includeDeprecated = args.Length > 2 && args[2].Equals("deprecated", StringComparison.OrdinalIgnoreCase);

            RunForEnum(matchingEnumType, input, command, includeDeprecated);
        }

        private void RunForEnum(Type enumType, string inputValue, CommandArguments command, bool includeDeprecated)
        {
            var enumName = GetDottedTypeName(enumType);

            if (Enum.TryParse(enumType, inputValue, out var enumValue))
            {
                command.Reply($"{Colors.LIGHTGRAY}{enumName}{Colors.NORMAL} ({Enum.Format(enumType, enumValue, "D")}) ={Colors.BLUE} {ExpandEnumFlagsToString(enumValue)}");

                return;
            }

            var enumValues = Enum.GetValues(enumType).Cast<object>();

            if (!includeDeprecated)
            {
                enumValues = enumValues.Except(enumValues.Where(x => enumType.GetMember(x.ToString())[0].GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0));
            }

            if (!string.IsNullOrEmpty(inputValue))
            {
                enumValues = enumValues.Where(x => x.ToString().Contains(inputValue, StringComparison.OrdinalIgnoreCase));
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

            var formatted = string.Join(", ", enumValues.Select(value => $"{Colors.BLUE}{value}{Colors.NORMAL} ({Enum.Format(enumType, value, "D")})"));

            if (count > 10)
            {
                formatted = $"{formatted}, and {count - 10} more...";
            }

            command.Reply($"{Colors.LIGHTGRAY}{enumName}{Colors.NORMAL}: {formatted}");
        }

        private static string ExpandEnumFlagsToString<TEnum>(TEnum enumValue)
        {
            if (typeof(TEnum).GetCustomAttributes(typeof(FlagsAttribute), false).Length > 0)
            {
                var definedFlags = new List<string>();
                var flags = Convert.ToUInt64(enumValue);
                ulong i = 0;
                var currentFlag = -1;

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
                        definedFlags.Add($"{Colors.RED}(1<<{currentFlag}){Colors.BLUE}");
                    }
                }

                // TODO: Handle odd flags that aren't (1<<x)

                if (definedFlags.Count > 0)
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
