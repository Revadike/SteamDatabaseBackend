/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class Utils
    {
        // Adapted from http://stackoverflow.com/a/13503860/139147
        // Mono doesn't really like method extensions
        public static IEnumerable<TResult> FullOuterJoin<TLeft, TRight, TKey, TResult>(
            /*            this*/ IEnumerable<TLeft> left,
            IEnumerable<TRight> right,
            Func<TLeft, TKey> leftKeySelector,
            Func<TRight, TKey> rightKeySelector,
            Func<TLeft, TRight, TKey, TResult> resultSelector,
            TLeft defaultLeft = default(TLeft),
            TRight defaultRight = default(TRight))
        {
            var leftLookup = left.ToLookup(leftKeySelector);
            var rightLookup = right.ToLookup(rightKeySelector);

            var leftKeys = leftLookup.Select(l => l.Key);
            var rightKeys = rightLookup.Select(r => r.Key);

            var keySet = new HashSet<TKey>(leftKeys.Union(rightKeys));

            return from key in keySet
                from leftValue in leftLookup[key].DefaultIfEmpty(defaultLeft)
                from rightValue in rightLookup[key].DefaultIfEmpty(defaultRight)
                select resultSelector(leftValue, rightValue, key);
        }

        public static SteamApps.PICSRequest NewPICSRequest(uint id)
        {
            return new SteamApps.PICSRequest(id, PICSTokens.GetToken(id), false);
        }

        public static SteamApps.PICSRequest NewPICSRequest(uint id, ulong accessToken)
        {
            if (accessToken > 0)
            {
                PICSTokens.HandleToken(id, accessToken);
            }

            return new SteamApps.PICSRequest(id, accessToken, false);
        }

        public static bool ConvertUserInputToSQLSearch(ref string output)
        {
            if (output.Length < 2 || !output.Distinct().Skip(1).Any()) // TODO: Probably would be better to only search for % and _ repetitions
            {
                return false;
            }

            if (output[0] == '^')
            {
                output = output.Substring(1);
            }
            else
            {
                output = "%" + output;
            }

            if (output[output.Length - 1] == '$')
            {
                output = output.Substring(0, output.Length - 1);
            }
            else
            {
                output += "%";
            }

            if (output.Length == 0)
            {
                return false;
            }

            var distinct = output.Distinct().ToList();

            if (distinct.Count() == 1 && distinct.First() == '%')
            {
                return false;
            }

            return true;
        }

        public static string RemoveControlCharacters(string input)
        {
            return new string(input.Where(c => !char.IsControl(c)).ToArray());
        }

        public static string JsonifyKeyValue(KeyValue keys)
        {
            string value;

            using (var sw = new StringWriter(new StringBuilder()))
            {
                using (JsonWriter w = new JsonTextWriter(sw))
                {
                    JsonifyKeyValue(w, keys.Children);
                }

                value = sw.ToString();
            }

            return value;
        }

        private static void JsonifyKeyValue(JsonWriter w, List<KeyValue> keys)
        {
            w.WriteStartObject();

            foreach (KeyValue keyval in keys)
            {
                if (keyval.Children.Count > 0)
                {
                    w.WritePropertyName(keyval.Name);
                    JsonifyKeyValue(w, keyval.Children);
                }
                else if (keyval.Value != null) // TODO: Should we be writing null keys anyway?
                {
                    w.WritePropertyName(keyval.Name);
                    w.WriteValue(keyval.Value);
                }
            }

            w.WriteEndObject();
        }
    }

    class EmptyGrouping<TKey, TValue> : IGrouping<TKey, TValue>
    {
        public TKey Key { get; set; }

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
        {
            return Enumerable.Empty<TValue>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return Enumerable.Empty<TValue>().GetEnumerator();
        }
    }
}
