#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using WarehouseManager.Integration;
using WarehouseManager.Inventory;

namespace WarehouseManager.Ordering
{
    internal static class OrderExecutor
    {
        private const float DebounceSeconds = 0.50f;
        private static float _lastPressRealtime;

        private static readonly KeyCode OrderKey = KeyCode.F1; // Shift+F1
        private const bool AutoCheckout = true;

        internal static void Update()
        {
            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!shiftHeld) return;

            if (Time.realtimeSinceStartup - _lastPressRealtime < DebounceSeconds)
                return;

            if (Input.GetKeyDown(OrderKey))
            {
                _lastPressRealtime = Time.realtimeSinceStartup;
                RunOrder_FromCatalog();
            }
        }

        internal static void RunOrder_FromCatalog()
        {
            try
            {
                Plugin.LogInfo("[WarehouseManager] Shift+F1 detected. Building order from catalog + inventory scans...");

                var catalog = LoadCatalogBigOnly();
                if (catalog.Count == 0)
                {
                    Plugin.LogWarn("[ORDER] Catalog empty or missing big-box entries. Build the catalog first.");
                    return;
                }

                if (!CPlayerData.m_IsScannerRestockUnlocked)
                {
                    Plugin.LogWarn("[ORDER] Scanner Restock is LOCKED. Unlock it in-game first (the $12k upgrade).");
                    return;
                }

                var screen = FindScannerRestockScreen();
                if (screen == null)
                {
                    Plugin.LogWarn("[ORDER] Could not find ScannerRestockScreen in scene. Open the Scanner Restock UI once, then try again.");
                    return;
                }
                                
                var wh = WarehouseInventoryScanner.Scan();
                var sh = ShopInventoryScanner.Scan();
                var loose = LooseBoxScanner.Scan();

                // Run header + scan summary (Verbose)
                Plugin.LogVerbose($"[ORDER][V] Run header: catalogBig={catalog.Count} autoCheckout={AutoCheckout}");
                Plugin.LogVerbose($"[ORDER][V] Scan counts: WH keys={wh.Count}, Shop keys={sh.Count}, Loose keys={loose.Count}");

                int looseTotalBoxes = 0, looseTotalBig = 0, looseTotalSmall = 0, looseTotalUnits = 0;
                foreach (var kv in loose)
                {
                    looseTotalBoxes += kv.Value.Boxes;
                    looseTotalBig += kv.Value.BigBoxes;
                    looseTotalSmall += kv.Value.SmallBoxes;
                    looseTotalUnits += kv.Value.UnitsCur;
                }
                Plugin.LogVerbose($"[ORDER][V] Loose totals: boxes={looseTotalBoxes} big={looseTotalBig} small={looseTotalSmall} unitsCur={looseTotalUnits}");

                // Only plan for things you actually have represented in the world right now.
                var allKeys = new HashSet<EItemType>();
                foreach (var k in wh.Keys) allKeys.Add(k);
                foreach (var k in sh.Keys) allKeys.Add(k);
                foreach (var k in loose.Keys) allKeys.Add(k);

                Plugin.LogVerbose($"[ORDER][V] allKeys={allKeys.Count}");

                int totalLinesPlanned = 0;
                int totalBoxesPlanned = 0;

                int skipNotInCatalog = 0;
                int skipNeedZero = 0;
                int skipNoSlots = 0;
                int skipOverfill = 0;
                int skipZeroBoxes = 0;

                var plan = new List<(EItemType itemType, int restockIndex, int boxes, int upb)>();

                foreach (var itemType in allKeys.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    if (!catalog.TryGetValue(itemType, out var rec))
                    {
                        skipNotInCatalog++;
                        Plugin.LogVerbose($"[ORDER][V] {itemType} SKIP: not in BIG catalog");
                        continue;
                    }

                    int upbBig = rec.UnitsPerBox;
                    if (upbBig <= 0)
                        continue;

                    sh.TryGetValue(itemType, out var shopT);
                    int shopCur = shopT.Current;
                    int shopMax = shopT.Max;

                    wh.TryGetValue(itemType, out var whT);
                    int whBigCur = whT.BoxesCurBig;
                    int whBigMax = whT.BoxesMaxBig;

                    loose.TryGetValue(itemType, out var looseT);
                    int looseUnitsCur = looseT.UnitsCur;

                    // big-only warehouse units
                    int whUnitsCur = whBigCur * upbBig;
                    int whUnitsMax = whBigMax * upbBig;

                    // RULE: Loose counts into ONHAND, NOT into MAX
                    int onHand = shopCur + whUnitsCur + looseUnitsCur;
                    int targetMax = shopMax + whUnitsMax;

                    int needUnits = targetMax - onHand;

                    // Must have warehouse big slot capacity remaining (reserve loose big boxes)
                    int reservedBigSlots = looseT.BigBoxes;
                    int bigSlotsFree = Math.Max(0, whBigMax - whBigCur - reservedBigSlots);

                    Plugin.LogVerbose(
                        $"[ORDER][V] {itemType} | shop {shopCur}/{shopMax} | WHbig {whBigCur}/{whBigMax} upb={upbBig} " +
                        $"| loose units={looseUnitsCur} looseBig={reservedBigSlots} | onHand={onHand} target={targetMax} need={needUnits} freeSlots={bigSlotsFree}"
                    );

                    if (needUnits <= 0)
                    {
                        skipNeedZero++;
                        Plugin.LogVerbose($"[ORDER][V] {itemType} SKIP: needUnits<=0");
                        continue;
                    }

                    if (bigSlotsFree <= 0)
                    {
                        skipNoSlots++;
                        Plugin.LogInfo(
                            $"[ORDER] {itemType}: needs {needUnits} units but no free BIG slots. " +
                            $"WH {whBigCur}/{whBigMax}, reserved(loose)={reservedBigSlots}."
                        );
                        continue;
                    }

                    // How many full big boxes can we add without overfilling?
                    int maxBoxesByNeed = needUnits / upbBig; // floor
                    if (maxBoxesByNeed <= 0)
                    {
                        skipOverfill++;
                        Plugin.LogInfo(
                            $"[ORDER] {itemType}: needs {needUnits} units but +1 big box ({upbBig}) would overfill ({onHand + upbBig}/{targetMax})."
                        );
                        continue;
                    }

                    int boxesToOrder = Math.Min(maxBoxesByNeed, bigSlotsFree);
                    if (boxesToOrder <= 0)
                    {
                        skipZeroBoxes++;
                        Plugin.LogVerbose($"[ORDER][V] {itemType} SKIP: boxesToOrder<=0 (maxByNeed={maxBoxesByNeed}, freeSlots={bigSlotsFree})");
                        continue;
                    }

                    Plugin.LogVerbose($"[ORDER][V] {itemType} PLAN: boxesToOrder={boxesToOrder} restockIdx={rec.RestockIndex}");
                    plan.Add((itemType, rec.RestockIndex, boxesToOrder, upbBig));
                }

                Plugin.LogVerbose($"[ORDER][V] Skip summary: notInCatalog={skipNotInCatalog}, needZero={skipNeedZero}, noSlots={skipNoSlots}, overfill={skipOverfill}, zeroBoxes={skipZeroBoxes}");
                Plugin.LogVerbose($"[ORDER][V] Plan lines={plan.Count}");

                if (plan.Count == 0)
                {
                    Plugin.LogInfo("[ORDER] No items need ordering (or ordering would overfill).");
                    return;
                }

                // Add to cart
                foreach (var line in plan)
                {
                    Plugin.LogInfo(
                        $"[ORDER] {line.itemType}: adding {line.boxes} BIG box(es) to cart " +
                        $"idx={line.restockIndex} upb={line.upb}"
                    );

                    try
                    {
                        screen.AddToCartForCheckout(line.restockIndex, line.boxes);
                        totalLinesPlanned++;
                        totalBoxesPlanned += line.boxes;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogWarn($"[ORDER] AddToCartForCheckout failed for {line.itemType} idx={line.restockIndex}: {ex.Message}");
                    }
                }

                Plugin.LogInfo($"[ORDER] Cart filled. Lines added={totalLinesPlanned}, boxes added={totalBoxesPlanned}.");

                // Auto-checkout
                if (AutoCheckout && totalBoxesPlanned > 0)
                {
                    Plugin.LogInfo("[ORDER] Auto-checkout enabled. Attempting checkout now...");
                    try
                    {
                        screen.OnPressCheckoutButton();
                        Plugin.LogInfo("[ORDER] Checkout attempted. If you had enough money, boxes should be spawning now.");
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogWarn($"[ORDER] OnPressCheckoutButton failed: {ex.Message}");
                    }

                    FatePostRunner.RunDelayed(8f);
                }
                else
                {
                    Plugin.LogInfo("[ORDER] Auto-checkout disabled. Cart is ready; open scanner restock UI and press checkout.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError($"[ORDER] RunOrder_FromCatalog failed: {ex}");
            }
        }

        // ============================================================
        // Find ScannerRestockScreen instance
        // ============================================================

        private static ScannerRestockScreen? FindScannerRestockScreen()
        {
            var all = Resources.FindObjectsOfTypeAll<ScannerRestockScreen>();
            if (all == null || all.Length == 0) return null;

            foreach (var s in all)
            {
                if (s != null && s.gameObject != null && s.gameObject.scene.IsValid())
                    return s;
            }

            return all[0];
        }

        // ============================================================
        // Catalog loading (big-only)
        // ============================================================

        private sealed class CatalogRecord
        {
            public string Key = "";
            public int RestockIndex;
            public int UnitsPerBox;
            public bool BigBox;
        }

        private static Dictionary<EItemType, CatalogRecord> LoadCatalogBigOnly()
        {
            var map = new Dictionary<EItemType, CatalogRecord>();

            string path = CatalogService.CatalogPath;
            if (!File.Exists(path))
            {
                Plugin.LogWarn($"[ORDER] Catalog not found: {path}");
                return map;
            }

            try
            {
                string raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw)) return map;

                foreach (var obj in SimpleJsonObjects(raw))
                {
                    string key = GetJsonString(obj, "key");
                    int idx = GetJsonInt(obj, "restockIndex", -1);
                    int upb = GetJsonInt(obj, "unitsPerBox", 0);
                    bool big = GetJsonBool(obj, "bigBox", false);

                    if (!big) continue;
                    if (idx < 0 || upb <= 0) continue;
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    if (!Enum.TryParse<EItemType>(key, true, out var itemType))
                        continue;

                    if (!map.TryGetValue(itemType, out var existing) || upb > existing.UnitsPerBox)
                    {
                        map[itemType] = new CatalogRecord
                        {
                            Key = key,
                            RestockIndex = idx,
                            UnitsPerBox = upb,
                            BigBox = true
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError($"[ORDER] Failed to read catalog: {ex}");
            }

            Plugin.LogInfo($"[ORDER] Loaded BIG-only catalog entries: {map.Count}");
            return map;
        }

        // ============================================================
        // Tiny JSON helpers
        // ============================================================

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
            return bool.TryParse(s, out var v) ? v : fallback;
        }

        private static string Unescape(string s)
            => (s ?? "").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
}
