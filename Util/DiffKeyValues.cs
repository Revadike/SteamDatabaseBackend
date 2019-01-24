/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class DiffKeyValues
    {
        public class ComparedValue
        {
            public const string TypeAdded = "added";
            public const string TypeRemoved = "removed";
            public const string TypeModified = "modified";

            public string OldValue { get; set; }
            public string NewValue { get; set; }
            public string Type { get; set; }
        }

        public static Dictionary<string, ComparedValue> Diff(string oldJson, KeyValue newKv)
        {
            var oldFlat = DeserializeAndFlattenJson(oldJson);
            var newFlat = FlattenKeyValue(newKv.Children, null); // Children because we skip root
            var diff = new Dictionary<string, ComparedValue>();

            foreach (var oldSingle in oldFlat)
            {
                if (newFlat.TryGetValue(oldSingle.Key, out var newValue))
                {
                    if (newValue != oldSingle.Value)
                    {
                        diff.Add(oldSingle.Key, new ComparedValue
                        {
                            Type = ComparedValue.TypeModified,
                            OldValue = oldSingle.Value,
                            NewValue = newValue
                        });
                    }
                }
                else
                {
                    diff.Add(oldSingle.Key, new ComparedValue
                    {
                        Type = ComparedValue.TypeRemoved,
                        OldValue = oldSingle.Value
                    });
                }
            }

            foreach (var newSingle in newFlat)
            {
                if (!oldFlat.ContainsKey(newSingle.Key))
                {
                    diff.Add(newSingle.Key, new ComparedValue
                    {
                        Type = ComparedValue.TypeAdded,
                        NewValue = newSingle.Value
                    });
                }
            }

            return diff;
        }

        private static Dictionary<string, string> FlattenKeyValue(KeyValue kv, string path = "")
        {
            var flat = new Dictionary<string, string>();
            var key = $"{path}{kv.Name}";

            if (kv.Children.Count > 0)
            {
                var flatChildren = FlattenKeyValue(kv.Children, key);

                foreach (var children in flatChildren)
                {
                    flat.Add(children.Key, children.Value);
                }
            }
            else if (kv.Value != null)
            {
                flat.Add(key, kv.Value);
            }

            return flat;
        }

        private static Dictionary<string, string> FlattenKeyValue(IEnumerable<KeyValue> input, string path)
        {
            if (path != null)
            {
                path += "/";
            }

            return input
                .Select(kv => FlattenKeyValue(kv, path))
                .SelectMany(flatChildren => flatChildren)
                .ToDictionary(children => children.Key, children => children.Value);
        }

        private static Dictionary<string, string> DeserializeAndFlattenJson(string json)
        {
            var dict = new Dictionary<string, string>();
            var token = JToken.Parse(json);
            FillDictionaryFromJToken(dict, token, null);
            return dict;
        }

        private static void FillDictionaryFromJToken(IDictionary<string, string> dict, JToken token, string prefix)
        {
            if (token.Type == JTokenType.Object)
            {
                foreach (var prop in token.Children<JProperty>())
                {
                    FillDictionaryFromJToken(dict, prop.Value, prefix == null ? prop.Name : $"{prefix}/{prop.Name}");
                }
            }
            else
            {
                dict.Add(prefix, ((JValue)token).Value.ToString());
            }
        }
    }
}
