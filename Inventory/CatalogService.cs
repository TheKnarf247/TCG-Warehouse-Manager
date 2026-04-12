#nullable enable

using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace WarehouseManager.Inventory
{
    internal static class CatalogService
    {
        private static bool _running;

        // Save in BepInEx/patchers/Warehouse Manager by default (easy to find)
        internal static string CatalogPath
        {
            get
            {
                string patchersRoot = Path.Combine(Paths.BepInExRootPath, "patchers");
                string wmDir = Path.Combine(patchersRoot, "Warehouse Manager");
                Directory.CreateDirectory(wmDir);
                return Path.Combine(wmDir, "WarehouseManager.catalog.json");
            }
        }

        internal static bool IsRunning => _running;
        internal static void SetRunning(bool running)
        {
            _running = running;
        }

        /// <summary>
        /// Readiness gate used by the LoadingScreen hook (prevents running too early).
        /// IMPORTANT: This must NOT touch CSingleton<T>.Instance (it can instantiate singletons early).
        /// </summary>
        internal static bool CanBuildNow()
        {
            // Only check if an InventoryBase object already exists (non-invasive).
            var invType = FindTypeByName("InventoryBase");
            if (invType == null) return false;

            var existing = Resources.FindObjectsOfTypeAll(invType);
            return existing != null && existing.Length > 0;
        }

        private static Type? FindTypeByName(string shortTypeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.Name == shortTypeName) return t;
                    if (t.FullName != null && t.FullName.EndsWith("." + shortTypeName, StringComparison.Ordinal))
                        return t;
                }
            }
            return null;
        }

        /// <summary>
        /// Merge mode: loads existing JSON, runs builder, adds only new items,
        /// and updates restockIndex for existing signatures (key+bigBox+upb).
        /// </summary>
        internal static void BuildOneShotMerge(MonoBehaviour? host)
        {
            if (OneShotCatalogBuilder.IsRunning)
            {
                Plugin.Logger.LogWarning("[CAT] Build already running.");
                return;
            }

            var resolvedHost = EnsureHost(host);
            if (resolvedHost == null) return;

            _running = true;
            Plugin.Logger.LogInfo($"[CAT] Starting one-shot catalog build (MERGE). Output => {CatalogPath}");

            OneShotCatalogBuilder.Start(
                host: resolvedHost, // ✅ use resolved host (do NOT pass the possibly-null original)
                log: Plugin.Logger,
                onComplete: (res) =>
                {
                    try
                    {
                        var existing = LoadExistingRecords();
                        int before = existing.Count;

                        int added = Merge(existing, res.Hits, out int updatedIdx);

                        SaveRecords(existing);

                        Plugin.Logger.LogInfo($"[CAT] Merge complete. Added={added}, UpdatedIdx={updatedIdx}, Total={existing.Count} (was {before}). Path={CatalogPath}");
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogError($"[CAT] Merge save failed: {ex}");
                    }
                    finally
                    {
                        _running = false;
                    }
                },
                maxIndexToProbe: 2000,
                stopAfterConsecutiveFails: 300,
                perIndexDelaySeconds: 0.001f
            );
        }

        // =========================
        // Merge model
        // =========================

        internal sealed class CatalogRecord
        {
            public string Key = "";
            public int RestockIndex;
            public int UnitsPerBox;
            public bool BigBox;
        }

        private static int Merge(List<CatalogRecord> existing, List<OneShotCatalogBuilder.CatalogHit> hits, out int updatedIdx)
        {
            updatedIdx = 0;

            // signature -> record
            var map = new Dictionary<string, CatalogRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in existing)
                map[MakeSig(r.Key, r.BigBox, r.UnitsPerBox)] = r;

            int added = 0;

            foreach (var h in hits)
            {
                if (string.IsNullOrWhiteSpace(h.Key)) continue;
                if (h.UnitsPerBox <= 0) continue;

                string sig = MakeSig(h.Key, h.BigBox, h.UnitsPerBox);

                if (map.TryGetValue(sig, out var rec))
                {
                    // Update index if it changed (keeps ordering accurate)
                    if (rec.RestockIndex != h.RestockIndex)
                    {
                        rec.RestockIndex = h.RestockIndex;
                        updatedIdx++;
                    }
                    continue;
                }

                var newRec = new CatalogRecord
                {
                    Key = h.Key,
                    RestockIndex = h.RestockIndex,
                    UnitsPerBox = h.UnitsPerBox,
                    BigBox = h.BigBox
                };

                existing.Add(newRec);
                map[sig] = newRec;
                added++;
            }

            return added;
        }

        private static string MakeSig(string key, bool bigBox, int upb)
            => $"{key}||{(bigBox ? "big" : "small")}||{upb}";

        // =========================
        // Load + Save JSON
        // =========================

        private static void SaveRecords(List<CatalogRecord> records)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);

            var ordered = records
                .Where(r => !string.IsNullOrWhiteSpace(r.Key) && r.UnitsPerBox > 0 && r.RestockIndex >= 0)
                .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.BigBox ? 0 : 1)
                .ThenBy(r => r.UnitsPerBox)
                .ToList();

            var sb = new StringBuilder();
            sb.Append("{\n  \"records\": [\n");

            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                sb.Append("    { ");
                sb.Append($"\"key\": \"{Escape(r.Key)}\", ");
                sb.Append($"\"restockIndex\": {r.RestockIndex}, ");
                sb.Append($"\"unitsPerBox\": {r.UnitsPerBox}, ");
                sb.Append($"\"bigBox\": {(r.BigBox ? "true" : "false")} ");
                sb.Append("}");
                sb.Append(i == ordered.Count - 1 ? "\n" : ",\n");
            }

            sb.Append("  ]\n}\n");
            File.WriteAllText(CatalogPath, sb.ToString(), Encoding.UTF8);
        }

        private static List<CatalogRecord> LoadExistingRecords()
        {
            var list = new List<CatalogRecord>();

            if (!File.Exists(CatalogPath))
                return list;

            try
            {
                string raw = File.ReadAllText(CatalogPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(raw))
                    return list;

                // Super simple parser for our own schema:
                // Extract objects with key/restockIndex/unitsPerBox/bigBox using string scanning.
                foreach (var obj in SimpleJsonObjects(raw))
                {
                    string key = GetJsonString(obj, "key");
                    int idx = GetJsonInt(obj, "restockIndex", -1);
                    int upb = GetJsonInt(obj, "unitsPerBox", 0);
                    bool big = GetJsonBool(obj, "bigBox", true);

                    if (string.IsNullOrWhiteSpace(key) || idx < 0 || upb <= 0) continue;

                    list.Add(new CatalogRecord
                    {
                        Key = key,
                        RestockIndex = idx,
                        UnitsPerBox = upb,
                        BigBox = big
                    });
                }
            }
            catch
            {
                // If the file is malformed, treat as empty rather than crashing.
            }

            return list;
        }

        // =========================
        // Tiny JSON helpers (tolerant, schema-specific)
        // =========================

        private static IEnumerable<string> SimpleJsonObjects(string json)
        {
            // Extract "{ ... }" blocks inside "records": [ ... ].
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

        private static string GetJsonString(string obj, string key)
        {
            var pat = $"\"{key}\"";
            int k = obj.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return "";

            int colon = obj.IndexOf(':', k);
            if (colon < 0) return "";

            int q1 = obj.IndexOf('"', colon + 1);
            if (q1 < 0) return "";

            int q2 = obj.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";

            return Unescape(obj.Substring(q1 + 1, q2 - q1 - 1));
        }

        private static int GetJsonInt(string obj, string key, int fallback)
        {
            var pat = $"\"{key}\"";
            int k = obj.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return fallback;

            int colon = obj.IndexOf(':', k);
            if (colon < 0) return fallback;

            int end = obj.IndexOfAny(new[] { ',', '}', '\n', '\r' }, colon + 1);
            if (end < 0) end = obj.Length;

            var s = obj.Substring(colon + 1, end - colon - 1).Trim();
            return int.TryParse(s, out var v) ? v : fallback;
        }

        private static bool GetJsonBool(string obj, string key, bool fallback)
        {
            var pat = $"\"{key}\"";
            int k = obj.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return fallback;

            int colon = obj.IndexOf(':', k);
            if (colon < 0) return fallback;

            int end = obj.IndexOfAny(new[] { ',', '}', '\n', '\r' }, colon + 1);
            if (end < 0) end = obj.Length;

            var s = obj.Substring(colon + 1, end - colon - 1).Trim();
            if (bool.TryParse(s, out var v)) return v;
            return fallback;
        }

        private static string Escape(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Unescape(string s)
            => (s ?? "").Replace("\\\"", "\"").Replace("\\\\", "\\");

        // =========================
        // Host helper
        // =========================

        private static MonoBehaviour? EnsureHost(MonoBehaviour? host)
        {
            if (host != null) return host;

            // Prefer the plugin instance if you expose it
            if (Plugin.Instance != null)
                return Plugin.Instance;

            // Fallback: find it
            var found = UnityEngine.Object.FindObjectOfType<Plugin>();
            if (found != null)
                return found;

            Plugin.Logger.LogError("[CAT] No coroutine host available (Plugin.Instance is null). Cannot start builder.");
            return null;
        }
    }
}
