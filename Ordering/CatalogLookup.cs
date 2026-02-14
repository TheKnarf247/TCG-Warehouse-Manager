#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WarehouseManager.Inventory;

namespace WarehouseManager.Ordering
{
    internal static class CatalogLookup
    {
        internal static Dictionary<EItemType, int> Load()
        {
            var dict = new Dictionary<EItemType, int>();

            string path = CatalogService.CatalogPath;

            if (!File.Exists(path))
            {
                Plugin.Logger.LogWarning("[ORDER] Catalog file missing.");
                return dict;
            }

            try
            {
                string raw = File.ReadAllText(path, Encoding.UTF8);

                foreach (var obj in SimpleJsonObjects(raw))
                {
                    string key = GetString(obj, "key");
                    int idx = GetInt(obj, "restockIndex");
                    int upb = GetInt(obj, "unitsPerBox");
                    bool big = GetBool(obj, "bigBox");

                    if (!big) continue; // big only policy

                    if (Enum.TryParse<EItemType>(key, out var item))
                    {
                        if (!dict.ContainsKey(item))
                            dict[item] = idx;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ORDER] Failed loading catalog: {ex}");
            }

            return dict;
        }

        // --- minimal JSON helpers ---

        private static IEnumerable<string> SimpleJsonObjects(string json)
        {
            int recIdx = json.IndexOf("\"records\"", StringComparison.OrdinalIgnoreCase);
            if (recIdx < 0) yield break;

            int arrStart = json.IndexOf('[', recIdx);
            if (arrStart < 0) yield break;

            int i = arrStart;
            while (i < json.Length)
            {
                int objStart = json.IndexOf('{', i);
                if (objStart < 0) yield break;

                int depth = 0;
                int j = objStart;
                for (; j < json.Length; j++)
                {
                    if (json[j] == '{') depth++;
                    else if (json[j] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            yield return json.Substring(objStart, j - objStart + 1);
                            i = j + 1;
                            break;
                        }
                    }
                }

                if (j >= json.Length) yield break;
            }
        }

        private static string GetString(string obj, string key)
        {
            var pat = $"\"{key}\"";
            int k = obj.IndexOf(pat);
            if (k < 0) return "";

            int colon = obj.IndexOf(':', k);
            int q1 = obj.IndexOf('"', colon + 1);
            int q2 = obj.IndexOf('"', q1 + 1);

            if (q1 < 0 || q2 < 0) return "";
            return obj.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static int GetInt(string obj, string key)
        {
            var pat = $"\"{key}\"";
            int k = obj.IndexOf(pat);
            if (k < 0) return -1;

            int colon = obj.IndexOf(':', k);
            int end = obj.IndexOfAny(new[] { ',', '}', '\n' }, colon + 1);
            if (end < 0) end = obj.Length;

            var s = obj.Substring(colon + 1, end - colon - 1).Trim();
            return int.TryParse(s, out var v) ? v : -1;
        }

        private static bool GetBool(string obj, string key)
        {
            var pat = $"\"{key}\"";
            int k = obj.IndexOf(pat);
            if (k < 0) return false;

            int colon = obj.IndexOf(':', k);
            int end = obj.IndexOfAny(new[] { ',', '}', '\n' }, colon + 1);
            if (end < 0) end = obj.Length;

            var s = obj.Substring(colon + 1, end - colon - 1).Trim();
            return bool.TryParse(s, out var v) && v;
        }
    }
}
