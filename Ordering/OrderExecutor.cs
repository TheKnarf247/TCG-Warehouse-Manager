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
    /// <summary>
    /// Normal ordering execution (big-only) using WarehouseManager.catalog.json.
    ///
    /// Hotkey:
    ///   Shift + F1  => Build an order plan and add the required BIG boxes to the cart, then optionally auto-checkout.
    ///
    /// Rules:
    /// - BIG ONLY (must be bigBox=true in the catalog)
    /// - NEVER over-order: if +1 box would make on-hand > target max, we do NOT order that box
    /// - LOOSE counts toward ONHAND, but does NOT contribute to MAX capacity
    /// - Cannot exceed warehouse BIG slot capacity (boxesCurBig/boxesMaxBig)
    /// - Uses JSON catalog (NOT InventoryBase restock list scanning)
    /// </summary>
    internal static class OrderExecutor
    {
        private const float DebounceSeconds = 0.50f;
        private static float _lastPressRealtime;

        private static readonly KeyCode OrderKey = KeyCode.F1; // Shift+F1

        // Flip this later to config; for now: auto-checkout when done adding lines.
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

        /// <summary>
        /// Main flow:
        /// 1) Load BIG-only catalog
        /// 2) Scan inventories (shop units, warehouse boxes, loose units)
        /// 3) Compute per-item boxes to order (big-only), respecting no-overfill + WH slot capacity
        /// 4) Add those boxes to ScannerRestockScreen cart
        /// 5) Auto-checkout (optional)
        /// </summary>
        internal static void RunOrder_FromCatalog()
        {
            try
            {
                Plugin.Logger.LogInfo("[WarehouseManager] Shift+F1 detected. Building order from catalog + inventory scans...");

                var catalog = LoadCatalogBigOnly();
                if (catalog.Count == 0)
                {
                    Plugin.Logger.LogWarning("[ORDER] Catalog empty or missing big-box entries. Build the catalog first.");
                    return;
                }

                if (!CPlayerData.m_IsScannerRestockUnlocked)
                {
                    Plugin.Logger.LogWarning("[ORDER] Scanner Restock is LOCKED. Unlock it in-game first (the $12k upgrade).");
                    return;
                }

                var screen = FindScannerRestockScreen();
                if (screen == null)
                {
                    Plugin.Logger.LogWarning("[ORDER] Could not find ScannerRestockScreen in scene. Open the Scanner Restock UI once, then try again.");
                    return;
                }

                FateHook.TryRun("PRE");

                var wh = WarehouseInventoryScanner.Scan();
                var sh = ShopInventoryScanner.Scan();
                var loose = LooseBoxScanner.Scan();

                // Only plan for things you actually have represented in the world right now.
                var allKeys = new HashSet<EItemType>();
                foreach (var k in wh.Keys) allKeys.Add(k);
                foreach (var k in sh.Keys) allKeys.Add(k);
                foreach (var k in loose.Keys) allKeys.Add(k);

                int totalLinesPlanned = 0;
                int totalBoxesPlanned = 0;

                // Build plan first (so we can log cleanly and checkout once)
                var plan = new List<(EItemType itemType, int restockIndex, int boxes, int upb)>();

                foreach (var itemType in allKeys.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    if (!catalog.TryGetValue(itemType, out var rec))
                        continue; // not in catalog big-only => can't order it (yet)

                    int upbBig = rec.UnitsPerBox;
                    if (upbBig <= 0) continue;

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
                    if (needUnits <= 0)
                        continue;

                    // Must have warehouse big slot capacity remaining
                    int bigSlotsFree = whBigMax - whBigCur;
                    if (bigSlotsFree <= 0)
                    {
                        Plugin.Logger.LogInfo($"[ORDER] {itemType}: needs {needUnits} units but no free BIG warehouse slots ({whBigCur}/{whBigMax}).");
                        continue;
                    }

                    // How many full big boxes can we add without overfilling?
                    int maxBoxesByNeed = needUnits / upbBig; // floor
                    if (maxBoxesByNeed <= 0)
                    {
                        Plugin.Logger.LogInfo($"[ORDER] {itemType}: needs {needUnits} units but +1 big box ({upbBig}) would overfill ({onHand + upbBig}/{targetMax}).");
                        continue;
                    }

                    int boxesToOrder = Math.Min(maxBoxesByNeed, bigSlotsFree);
                    if (boxesToOrder <= 0)
                        continue;

                    plan.Add((itemType, rec.RestockIndex, boxesToOrder, upbBig));
                }

                if (plan.Count == 0)
                {
                    Plugin.Logger.LogInfo("[ORDER] No items need ordering (or ordering would overfill).");
                    return;
                }

                // Add to cart
                foreach (var line in plan)
                {
                    Plugin.Logger.LogInfo(
                        $"[ORDER] {line.itemType}: adding {line.boxes} BIG box(es) to cart " +
                        $"idx={line.restockIndex} upb={line.upb}"
                    );

                    try
                    {
                        // This is the exact game method you pasted from dnSpy.
                        screen.AddToCartForCheckout(line.restockIndex, line.boxes);

                        totalLinesPlanned++;
                        totalBoxesPlanned += line.boxes;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning($"[ORDER] AddToCartForCheckout failed for {line.itemType} idx={line.restockIndex}: {ex.Message}");
                    }
                }

                Plugin.Logger.LogInfo($"[ORDER] Cart filled. Lines added={totalLinesPlanned}, boxes added={totalBoxesPlanned}.");

                // Auto-checkout
                if (AutoCheckout && totalBoxesPlanned > 0)
                {
                    Plugin.Logger.LogInfo("[ORDER] Auto-checkout enabled. Attempting checkout now...");
                    try
                    {
                        screen.OnPressCheckoutButton(); // handles money check + spawn + save + sounds
                        Plugin.Logger.LogInfo("[ORDER] Checkout attempted. If you had enough money, boxes should be spawning now.");
                    }
                    catch (Exception ex)                  
                    {
                        Plugin.Logger.LogWarning($"[ORDER] OnPressCheckoutButton failed: {ex.Message}");
                    }
                FatePostRunner.RunDelayed(8f);
                }
                else
                {
                    Plugin.Logger.LogInfo("[ORDER] Auto-checkout disabled. Cart is ready; open scanner restock UI and press checkout.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ORDER] RunOrder_FromCatalog failed: {ex}");
            }
        }

        // ============================================================
        // Find ScannerRestockScreen instance (the actual UI controller)
        // ============================================================

        private static ScannerRestockScreen? FindScannerRestockScreen()
        {
            // Works even if inactive in hierarchy.
            var all = Resources.FindObjectsOfTypeAll<ScannerRestockScreen>();
            if (all == null || all.Length == 0) return null;

            // Prefer an active one if available.
            foreach (var s in all)
            {
                if (s != null && s.gameObject != null && s.gameObject.scene.IsValid())
                    return s;
            }

            return all[0];
        }

        // ============================================================
        // Catalog loading (big-only, JSON schema: { "records":[ ... ] })
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
                Plugin.Logger.LogWarning($"[ORDER] Catalog not found: {path}");
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

                    if (!big) continue;                 // BIG ONLY
                    if (idx < 0 || upb <= 0) continue;
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    if (!Enum.TryParse<EItemType>(key, true, out var itemType))
                        continue;

                    // If duplicates exist, prefer the higher UPB big entry
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
                Plugin.Logger.LogError($"[ORDER] Failed to read catalog: {ex}");
            }

            Plugin.Logger.LogInfo($"[ORDER] Loaded BIG-only catalog entries: {map.Count}");
            return map;
        }

        // ============================================================
        // Tiny JSON helpers (schema-specific, tolerant)
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
            if (bool.TryParse(s, out var v)) return v;
            return fallback;
        }

        private static string Unescape(string s)
            => (s ?? "").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
}
