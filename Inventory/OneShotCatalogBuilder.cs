#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WarehouseManager.Inventory
{
    /// <summary>
    /// One-shot catalog builder: probes restock indices [0..maxIndex) and records valid entries.
    /// Uses InventoryBase.GetRestockData(index) + RestockManager.GetMaxItemCountInBox(itemType, isBigBox).
    /// This works for vanilla + EPL (as your trace proved).
    /// </summary>
    internal static class OneShotCatalogBuilder
    {
        internal sealed class CatalogHit
        {
            public string Key = "";          // RestockData.itemType string
            public int RestockIndex;         // index we probed
            public bool BigBox;              // RestockData.isBigBox
            public int UnitsPerBox;          // RestockManager.GetMaxItemCountInBox(itemType, bigBox)
            public int ItemTypeInt;          // numeric enum value (useful for debugging)
        }

        internal sealed class BuildResult
        {
            public readonly List<CatalogHit> Hits = new();
            public int Attempts;
            public int Successes;
            public int StopIndex;
            public int ConsecutiveFailsAtStop;
            public int MaxIndexProbed;
        }

        private static MethodInfo? _miGetRestockData;   // InventoryBase.GetRestockData(int)
        private static MethodInfo? _miGetMaxInBox;      // RestockManager.GetMaxItemCountInBox(EItemType, bool)

        internal static bool IsRunning { get; private set; }

        internal static Coroutine? Start(
            MonoBehaviour host,
            ManualLogSource log,
            Action<BuildResult> onComplete,
            int maxIndexToProbe = 2000,
            int stopAfterConsecutiveFails = 250,
            float perIndexDelaySeconds = 0.001f
        )
        {
            if (IsRunning)
            {
                log.LogWarning("[CAT] One-shot catalog build already running.");
                return null;
            }

            EnsureMethods(log);
            if (_miGetRestockData == null || _miGetMaxInBox == null)
            {
                log.LogError("[CAT] Required methods not found. Cannot build catalog.");
                return null;
            }

            IsRunning = true;

            return host.StartCoroutine(RunCoroutine(
                log,
                onComplete,
                maxIndexToProbe,
                stopAfterConsecutiveFails,
                perIndexDelaySeconds
            ));
        }

        private static IEnumerator RunCoroutine(
            ManualLogSource log,
            Action<BuildResult> onComplete,
            int maxIndexToProbe,
            int stopAfterConsecutiveFails,
            float perIndexDelaySeconds
        )
        {
            var result = new BuildResult { MaxIndexProbed = maxIndexToProbe };

            // Dedupe by key+bigbox+upb
            var dedupe = new Dictionary<string, CatalogHit>(StringComparer.OrdinalIgnoreCase);

            int consecutiveFails = 0;

            for (int idx = 0; idx < maxIndexToProbe; idx++)
            {
                result.Attempts++;

                object? restockData = null;
                try
                {
                    restockData = _miGetRestockData!.Invoke(null, new object[] { idx });
                }
                catch
                {
                    restockData = null;
                }

                if (restockData == null)
                {
                    consecutiveFails++;
                    if (consecutiveFails >= stopAfterConsecutiveFails)
                    {
                        result.StopIndex = idx;
                        result.ConsecutiveFailsAtStop = consecutiveFails;
                        break;
                    }

                    if (perIndexDelaySeconds > 0f)
                        yield return new WaitForSecondsRealtime(perIndexDelaySeconds);

                    continue;
                }

                object? itemTypeEnumObj = GetMemberValue(restockData, "itemType", "m_ItemType");
                if (itemTypeEnumObj == null)
                {
                    consecutiveFails++;
                    if (consecutiveFails >= stopAfterConsecutiveFails)
                    {
                        result.StopIndex = idx;
                        result.ConsecutiveFailsAtStop = consecutiveFails;
                        break;
                    }

                    if (perIndexDelaySeconds > 0f)
                        yield return new WaitForSecondsRealtime(perIndexDelaySeconds);

                    continue;
                }

                string key = itemTypeEnumObj.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(key) || IsNoneKey(key))
                {
                    consecutiveFails++;
                    if (consecutiveFails >= stopAfterConsecutiveFails)
                    {
                        result.StopIndex = idx;
                        result.ConsecutiveFailsAtStop = consecutiveFails;
                        break;
                    }

                    if (perIndexDelaySeconds > 0f)
                        yield return new WaitForSecondsRealtime(perIndexDelaySeconds);

                    continue;
                }

                bool bigBox = ReadBoolMember(restockData, "isBigBox", "m_IsBigBox", "m_isBigBox");

                int upb = 0;
                try
                {
                    object? upbObj = _miGetMaxInBox!.Invoke(null, new object[] { itemTypeEnumObj, bigBox });
                    if (upbObj != null) upb = Convert.ToInt32(upbObj);
                }
                catch
                {
                    upb = 0;
                }

                if (upb <= 0)
                {
                    consecutiveFails++;
                    if (consecutiveFails >= stopAfterConsecutiveFails)
                    {
                        result.StopIndex = idx;
                        result.ConsecutiveFailsAtStop = consecutiveFails;
                        break;
                    }

                    if (perIndexDelaySeconds > 0f)
                        yield return new WaitForSecondsRealtime(perIndexDelaySeconds);

                    continue;
                }

                consecutiveFails = 0;
                result.Successes++;

                int itemTypeInt = SafeToInt(itemTypeEnumObj);

                string dkey = $"{key}||{bigBox}||{upb}";
                if (!dedupe.TryGetValue(dkey, out var hit))
                {
                    hit = new CatalogHit
                    {
                        Key = key,
                        RestockIndex = idx,
                        BigBox = bigBox,
                        UnitsPerBox = upb,
                        ItemTypeInt = itemTypeInt
                    };
                    dedupe[dkey] = hit;
                }
                else
                {
                    // Prefer lowest index if duplicates
                    if (idx < hit.RestockIndex)
                        hit.RestockIndex = idx;
                }

                if (perIndexDelaySeconds > 0f)
                    yield return new WaitForSecondsRealtime(perIndexDelaySeconds);
            }

            result.Hits.AddRange(dedupe.Values);

            result.Hits.Sort((a, b) =>
            {
                int c = string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = a.BigBox.CompareTo(b.BigBox);
                if (c != 0) return c;
                return a.UnitsPerBox.CompareTo(b.UnitsPerBox);
            });

            log.LogInfo($"[CAT] One-shot build done. attempts={result.Attempts} successes={result.Successes} variants={result.Hits.Count} stopAtIdx={result.StopIndex} stopFails={result.ConsecutiveFailsAtStop}");

            IsRunning = false;
            onComplete(result);
        }

        private static void EnsureMethods(ManualLogSource log)
        {
            if (_miGetRestockData == null)
            {
                var invBase = AccessTools.TypeByName("InventoryBase");
                _miGetRestockData = invBase != null ? AccessTools.Method(invBase, "GetRestockData", new[] { typeof(int) }) : null;
                if (_miGetRestockData == null)
                    log.LogWarning("[CAT] Could not find InventoryBase.GetRestockData(int).");
            }

            if (_miGetMaxInBox == null)
            {
                var rm = AccessTools.TypeByName("RestockManager");
                _miGetMaxInBox = rm != null ? AccessTools.Method(rm, "GetMaxItemCountInBox") : null;
                if (_miGetMaxInBox == null)
                    log.LogWarning("[CAT] Could not find RestockManager.GetMaxItemCountInBox(...).");
            }
        }

        private static object? GetMemberValue(object obj, params string[] names)
        {
            var t = obj.GetType();
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var n in names)
            {
                var f = t.GetField(n, BF);
                if (f != null)
                {
                    try { return f.GetValue(obj); } catch { }
                }

                var p = t.GetProperty(n, BF);
                if (p != null && p.CanRead)
                {
                    try { return p.GetValue(obj, null); } catch { }
                }
            }

            return null;
        }

        private static bool ReadBoolMember(object obj, params string[] names)
        {
            var t = obj.GetType();
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var n in names)
            {
                var f = t.GetField(n, BF);
                if (f != null)
                {
                    try
                    {
                        var v = f.GetValue(obj);
                        if (v is bool b) return b;
                        if (v != null && bool.TryParse(v.ToString(), out bool parsed)) return parsed;
                    }
                    catch { }
                }

                var p = t.GetProperty(n, BF);
                if (p != null && p.CanRead)
                {
                    try
                    {
                        var v = p.GetValue(obj, null);
                        if (v is bool b) return b;
                        if (v != null && bool.TryParse(v.ToString(), out bool parsed)) return parsed;
                    }
                    catch { }
                }
            }

            return false;
        }

        private static int SafeToInt(object? v)
        {
            try
            {
                if (v is int i) return i;
                if (v is long l) return (int)l;
                if (v is double d) return (int)d;
                if (v == null) return 0;
                if (int.TryParse(v.ToString(), out int parsed)) return parsed;
            }
            catch { }
            return 0;
        }

        private static bool IsNoneKey(string key)
        {
            return key.Equals("None", StringComparison.OrdinalIgnoreCase)
                || key.Equals("0", StringComparison.OrdinalIgnoreCase)
                || key.Equals("null", StringComparison.OrdinalIgnoreCase);
        }
    }
}
