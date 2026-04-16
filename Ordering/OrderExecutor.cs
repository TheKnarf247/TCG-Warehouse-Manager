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
        private static bool _executionRoutineRunning;
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
                if (_executionRoutineRunning)
                {
                    Plugin.LogWarn("[ORDER] Skipped: order execution already in progress.");
                    return;
                }

                Plugin.LogInfo("[WarehouseManager] Shift+F1 detected. Building order from catalog + inventory scans...");

                var catalog = LoadCatalogAllSizes();
                if (CatalogService.IsRunning || OneShotCatalogBuilder.IsRunning)
                {
                    Plugin.Logger.LogInfo("[ORDER] Skipped: catalog build still running.");
                    return;
                }
                if (catalog.Count == 0)
                {
                    Plugin.LogWarn("[ORDER] Catalog empty or missing usable entries. Build the catalog first.");
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

                var plan = new List<(EItemType itemType, int restockIndex, int boxes, int upb, bool bigBox)>();

                foreach (var itemType in allKeys.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    if (!catalog.TryGetValue(itemType, out var records) || records == null || records.Count == 0)
                    {
                        skipNotInCatalog++;
                        Plugin.LogVerbose($"[ORDER][V] {itemType} SKIP: not in catalog");
                        continue;
                    }

                    
                    sh.TryGetValue(itemType, out var shopT);
                    int shopCur = shopT.Current;
                    int shopMax = shopT.Max;

                    wh.TryGetValue(itemType, out var whT);
                    loose.TryGetValue(itemType, out var looseT);
                    int looseUnitsCur = looseT.UnitsCur;

                    bool useBig;
                    if (whT.BoxesMaxBig > 0)
                        useBig = true;
                    else if (whT.BoxesMaxSmall > 0)
                        useBig = false;
                    else
                    {
                        skipNoSlots++;
                        Plugin.LogVerbose($"[ORDER][V] {itemType} SKIP: no warehouse slot size configured");
                        continue;
                    }

                    var rec = SelectCatalogRecordForShelfSize(catalog, itemType, useBig);
                    if (rec == null)
                    {
                        skipNotInCatalog++;
                        Plugin.LogInfo($"[ORDER] {itemType}: no catalog entry for {(useBig ? "BIG" : "SMALL")} box size.");
                        continue;
                    }
                    
                    if (!IsRestockLicenseUnlocked(rec.RestockIndex))
                    {
                        Plugin.LogInfo(
                            $"[ORDER] {itemType}: skipped, " +
                            $"{(useBig ? "BIG" : "SMALL")} restock entry locked (idx={rec.RestockIndex})."
                        );
                        continue;
                    }

                    int upb = rec.UnitsPerBox;
                    if (upb <= 0)
                        continue;

                    int whCur = useBig ? whT.BoxesCurBig : whT.BoxesCurSmall;
                    int whMax = useBig ? whT.BoxesMaxBig : whT.BoxesMaxSmall;
                    int reservedSlots = useBig ? looseT.BigBoxes : looseT.SmallBoxes;

                    int whUnitsCur = whCur * upb;
                    int whUnitsMax = whMax * upb;

                    int onHand = shopCur + whUnitsCur + looseUnitsCur;
                    int targetMax = shopMax + whUnitsMax;
                    int needUnits = targetMax - onHand;

                    int slotsFree = Math.Max(0, whMax - whCur - reservedSlots);

                    Plugin.LogVerbose(
                        $"[ORDER][V] {itemType} | shop {shopCur}/{shopMax} | " +
                        $"WH{(useBig ? "big" : "small")} {whCur}/{whMax} upb={upb} | " +
                        $"loose units={looseUnitsCur} loose{(useBig ? "Big" : "Small")}={reservedSlots} | " +
                        $"onHand={onHand} target={targetMax} need={needUnits} freeSlots={slotsFree}"
                    );

                    if (needUnits <= 0)
                    {
                        skipNeedZero++;
                        Plugin.LogVerbose($"[ORDER][V] {itemType} SKIP: needUnits<=0");
                        continue;
                    }

                    if (slotsFree <= 0)
                    {
                        skipNoSlots++;
                        Plugin.LogInfo(
                            $"[ORDER] {itemType}: needs {needUnits} units but no free {(useBig ? "BIG" : "SMALL")} slots. " +
                            $"WH {whCur}/{whMax}, reserved(loose)={reservedSlots}."
                        );
                        continue;
                    }

                    int maxBoxesByNeed = needUnits / upb;
                    if (maxBoxesByNeed <= 0)
                    {
                        skipOverfill++;
                        Plugin.LogInfo(
                            $"[ORDER] {itemType}: needs {needUnits} units but +1 {(useBig ? "big" : "small")} box ({upb}) " +
                            $"would overfill ({onHand + upb}/{targetMax})."
                        );
                        continue;
                    }

                    int boxesToOrder = Math.Min(maxBoxesByNeed, slotsFree);
                    if (boxesToOrder <= 0)
                    {
                        skipZeroBoxes++;
                        Plugin.LogVerbose($"[ORDER][V] {itemType} SKIP: boxesToOrder<=0 (maxByNeed={maxBoxesByNeed}, freeSlots={slotsFree})");
                        continue;
                    }

                    Plugin.LogVerbose(
                        $"[ORDER][V] {itemType} PLAN: boxesToOrder={boxesToOrder} " +
                        $"restockIdx={rec.RestockIndex} size={(useBig ? "BIG" : "SMALL")}"
                    );

                    plan.Add((itemType, rec.RestockIndex, boxesToOrder, upb, useBig));
                }

                Plugin.LogVerbose($"[ORDER][V] Skip summary: notInCatalog={skipNotInCatalog}, needZero={skipNeedZero}, noSlots={skipNoSlots}, overfill={skipOverfill}, zeroBoxes={skipZeroBoxes}");
                Plugin.LogVerbose($"[ORDER][V] Plan lines={plan.Count}");

                if (plan.Count == 0)
                {
                    Plugin.LogInfo("[ORDER] No items need ordering (or ordering would overfill).");
                    return;
                }

                // Add to cart
                Plugin.LogInfo($"[ORDER] Plan ready. Lines={plan.Count}. Starting staged cart execution...");
                StartExecutionRoutine(screen, plan);

                Plugin.LogInfo($"[ORDER] Cart filled. Lines added={totalLinesPlanned}, boxes added={totalBoxesPlanned}.");

                // Auto-checkout
                if (AutoCheckout && totalBoxesPlanned > 0)
                {
                    Plugin.LogInfo("[ORDER] Auto-checkout enabled. Attempting checkout now...");

                    if (CPlayerData.m_CoinAmountDouble < (double)screen.m_TotalCost)
                    {
                        Plugin.LogWarn(
                            $"[ORDER] Not enough money for checkout. " +
                            $"Need {screen.m_TotalCost:0.00}, have {CPlayerData.m_CoinAmountDouble:0.00}. Clearing cart."
                        );

                        NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                        SoundManager.GenericCancel(1f, 1f);
                        ClearScannerCheckoutCart(screen);
                    }
                    else
                    {
                        try
                        {
                            screen.OnPressCheckoutButton();
                            Plugin.LogInfo("[ORDER] Checkout attempted. Boxes should be spawning now.");
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogWarn($"[ORDER] Checkout failed unexpectedly: {ex.Message}");
                        }
                        finally
                        {
                            ClearScannerCheckoutCart(screen);
                        }
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
        // Helpers
        // ============================================================

        private static bool IsRestockLicenseUnlocked(int restockIndex)
        {
            try
            {
                if (restockIndex < 0)
                    return false;

                return CPlayerData.GetIsItemLicenseUnlocked(restockIndex);
            }
            catch (Exception ex)
            {
                Plugin.LogWarn($"[ORDER] Restock license check failed for idx={restockIndex}: {ex.Message}");
                return false;
            }
        }
        private static void ClearScannerCheckoutCart(ScannerRestockScreen screen)
        {
            try
            {
                if (screen == null)
                    return;

                while (screen.m_RestockIndexList != null &&
                       screen.m_RestockBoxCountList != null &&
                       screen.m_RestockIndexList.Count > 0 &&
                       screen.m_RestockBoxCountList.Count > 0)
                {
                    int index = screen.m_RestockIndexList[0];
                    int count = screen.m_RestockBoxCountList[0];

                    screen.RemoveFromCartForCheckout(index, count);
                }

                Plugin.LogInfo("[ORDER] Scanner checkout cart cleared.");
            }
            catch (Exception ex)
            {
                Plugin.LogWarn($"[ORDER] Failed to clear scanner checkout cart: {ex}");
            }
        }

        private static void StartExecutionRoutine(
            ScannerRestockScreen screen,
            List<(EItemType itemType, int restockIndex, int boxes, int upb, bool bigBox)> plan)
        {
            if (_executionRoutineRunning)
            {
                Plugin.LogWarn("[ORDER] Execution routine already running.");
                return;
            }

            if (Plugin.Instance == null)
            {
                Plugin.LogWarn("[ORDER] Cannot start execution routine: Plugin.Instance is null.");
                return;
            }

            Plugin.Instance.StartCoroutine(ExecutePlanRoutine(screen, plan));
        }

        private static System.Collections.IEnumerator ExecutePlanRoutine(
            ScannerRestockScreen screen,
            List<(EItemType itemType, int restockIndex, int boxes, int upb, bool bigBox)> plan)
        {
            _executionRoutineRunning = true;

            int totalLinesAdded = 0;
            int totalBoxesAdded = 0;

            try
            {
                // Add one line per frame
                foreach (var line in plan)
                {
                    Plugin.LogInfo(
                        $"[ORDER] {line.itemType}: adding {line.boxes} {(line.bigBox ? "BIG" : "SMALL")} box(es) to cart " +
                        $"idx={line.restockIndex} upb={line.upb}"
                    );

                    try
                    {
                        screen.AddToCartForCheckout(line.restockIndex, line.boxes);
                        totalLinesAdded++;
                        totalBoxesAdded += line.boxes;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogWarn($"[ORDER] AddToCartForCheckout failed for {line.itemType} idx={line.restockIndex}: {ex.Message}");
                    }

                    yield return null; // spread add-to-cart over frames
                }

                Plugin.LogInfo($"[ORDER] Cart filled. Lines added={totalLinesAdded}, boxes added={totalBoxesAdded}.");

                if (AutoCheckout && totalBoxesAdded > 0)
                {
                    // Give UI/cart totals one frame to settle
                    yield return null;

                    Plugin.LogInfo("[ORDER] Auto-checkout enabled. Attempting checkout now...");

                    if (CPlayerData.m_CoinAmountDouble < (double)screen.m_TotalCost)
                    {
                        Plugin.LogWarn(
                            $"[ORDER] Not enough money for checkout. " +
                            $"Need {screen.m_TotalCost:0.00}, have {CPlayerData.m_CoinAmountDouble:0.00}. Clearing cart."
                        );

                        NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                        SoundManager.GenericCancel(1f, 1f);
                        ClearScannerCheckoutCart(screen);
                    }
                    else
                    {
                        try
                        {
                            screen.OnPressCheckoutButton();
                            Plugin.LogInfo("[ORDER] Checkout attempted. Boxes should be spawning now.");
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogWarn($"[ORDER] Checkout failed unexpectedly: {ex.Message}");
                        }
                        finally
                        {
                            ClearScannerCheckoutCart(screen);
                        }
                    }

                    FatePostRunner.RunDelayed(8f);
                }
                else
                {
                    Plugin.LogInfo("[ORDER] Auto-checkout disabled. Cart is ready; open scanner restock UI and press checkout.");
                }
            }
            finally
            {
                _executionRoutineRunning = false;
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
        // Catalog loading (all sizes)
        // ============================================================

        private sealed class CatalogRecord
        {
            public string Key = "";
            public int RestockIndex;
            public int UnitsPerBox;
            public bool BigBox;
        }

        private static Dictionary<EItemType, List<CatalogRecord>> LoadCatalogAllSizes()
        {
            var map = new Dictionary<EItemType, List<CatalogRecord>>();

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

                    if (idx < 0 || upb <= 0) continue;
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    if (!Enum.TryParse<EItemType>(key, true, out var itemType))
                        continue;

                    if (!map.TryGetValue(itemType, out var list))
                    {
                        list = new List<CatalogRecord>();
                        map[itemType] = list;
                    }

                    bool replaced = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].BigBox == big)
                        {
                            if (upb > list[i].UnitsPerBox)
                            {
                                list[i] = new CatalogRecord
                                {
                                    Key = key,
                                    RestockIndex = idx,
                                    UnitsPerBox = upb,
                                    BigBox = big
                                };
                            }

                            replaced = true;
                            break;
                        }
                    }

                    if (!replaced)
                    {
                        list.Add(new CatalogRecord
                        {
                            Key = key,
                            RestockIndex = idx,
                            UnitsPerBox = upb,
                            BigBox = big
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError($"[ORDER] Failed to read catalog: {ex}");
            }

            Plugin.LogInfo($"[ORDER] Loaded catalog item types: {map.Count}");
            return map;
        }

        private static CatalogRecord? SelectCatalogRecordForShelfSize(
            Dictionary<EItemType, List<CatalogRecord>> catalog,
            EItemType itemType,
            bool wantBigBox)
        {
            if (!catalog.TryGetValue(itemType, out var list) || list == null)
                return null;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].BigBox == wantBigBox)
                    return list[i];
            }

            return null;
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
