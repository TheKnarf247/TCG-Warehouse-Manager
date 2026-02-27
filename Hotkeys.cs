#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using WarehouseManager.Inventory;
using WarehouseManager.Ordering;
// If your renamed config/logger are in different namespaces, adjust these:
using WarehouseManager.Config;     // WMConfig
using WarehouseManager.Logging;    // WMFileLogger

namespace WarehouseManager
{
    internal static class Hotkey
    {
        private static float _lastPressTime;
        private const float DebounceSeconds = 0.50f;

        internal static void Update()
        {
            // Master enable
            if (!WMConfig.Enabled.Value)
                return;

            // Debounce to prevent double-runs / held-key repeats
            if (Time.realtimeSinceStartup - _lastPressTime < DebounceSeconds)
                return;

            // Manual order execution hotkey (user-configurable)
            if (WMConfig.ManualHotkey.Value.IsDown())
            {
                _lastPressTime = Time.realtimeSinceStartup;

                LogInfo("[WarehouseManager] Manual hotkey detected. Executing auto-order...");
                Plugin.RunWithIntegrations("ManualHotkey", () => OrderExecutor.RunOrder_FromCatalog());
                return;
            }

            // Rebuild database/catalog hotkey (user-configurable)
            if (WMConfig.RebuildDatabaseHotkey.Value.IsDown())
            {
                _lastPressTime = Time.realtimeSinceStartup;

                LogInfo("[WarehouseManager] Rebuild database hotkey detected. Building one-shot catalog...");
                CatalogService.BuildOneShotMerge(Plugin.Instance);
                return;
            }

            // Optional debug: combined scan hotkey (only if debug enabled)
            if (WMConfig.DebugEnabled.Value && WMConfig.DebugCombinedScanHotkey.Value.IsDown())
            {
                _lastPressTime = Time.realtimeSinceStartup;

                LogInfo("[WarehouseManager] Debug scan hotkey detected. Running combined scan...");
                DumpCombinedInventory();
                return;
            }
        }

        private static void DumpCombinedInventory()
        {
            try
            {
                var wh = WarehouseInventoryScanner.Scan();
                var sh = ShopInventoryScanner.Scan();
                var loose = LooseBoxScanner.Scan();

                LogInfo($"[WM] Scan counts: shopKeys={sh.Count}, whKeys={wh.Count}, looseKeys={loose.Count}");

                var allKeys = new HashSet<EItemType>();
                foreach (var k in wh.Keys) allKeys.Add(k);
                foreach (var k in sh.Keys) allKeys.Add(k);
                foreach (var k in loose.Keys) allKeys.Add(k);

                // Keep all WM artifacts under patchers/Warehouse Manager (matches your other paths)
                string dir = Path.Combine(Paths.BepInExRootPath, "patchers", "Warehouse Manager", "debug");
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, $"inventory_combined_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                var sb = new StringBuilder();
                sb.AppendLine("Combined Inventory Scan (Shop + Warehouse + Loose Boxes)");
                sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("====================================================");
                sb.AppendLine("Warehouse counts are BOXES stored in compartments (big vs small inferred by max-per-slot).");
                sb.AppendLine("Shop counts are UNITS on shop shelves.");
                sb.AppendLine("Loose counts are UNITS in packaging boxes not stored on shelves (floor / carried / etc).");
                sb.AppendLine("====================================================");
                sb.AppendLine();

                foreach (var itemType in allKeys.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    var pack = PackagingInfo.Get(itemType);

                    sh.TryGetValue(itemType, out var shopT);
                    wh.TryGetValue(itemType, out var whT);
                    loose.TryGetValue(itemType, out var looseT);

                    int shopCur = shopT.Current;
                    int shopMax = shopT.Max;

                    int whBigCur = whT.BoxesCurBig;
                    int whBigMax = whT.BoxesMaxBig;
                    int whSmallCur = whT.BoxesCurSmall;
                    int whSmallMax = whT.BoxesMaxSmall;

                    int looseUnitsCur = looseT.UnitsCur;
                    int looseUnitsMax = looseT.UnitsMax;

                    int whUnitsCur = 0;
                    int whUnitsMax = 0;

                    if (pack.UpbBig > 0)
                    {
                        whUnitsCur += whBigCur * pack.UpbBig;
                        whUnitsMax += whBigMax * pack.UpbBig;
                    }
                    if (pack.UpbSmall > 0)
                    {
                        whUnitsCur += whSmallCur * pack.UpbSmall;
                        whUnitsMax += whSmallMax * pack.UpbSmall;
                    }

                    int totalUnitsCur = shopCur + looseUnitsCur + whUnitsCur;
                    int totalUnitsMax = shopMax + whUnitsMax;

                    sb.AppendLine(itemType.ToString());
                    sb.AppendLine($"  Packaging : {pack.BoxTypeText} (UPB big={pack.UpbBig}, small={pack.UpbSmall})");
                    sb.AppendLine($"  SHOP      : {shopCur} / {shopMax} units");
                    sb.AppendLine(
                        $"  WAREHOUSE : big {whBigCur}/{whBigMax} boxes, small {whSmallCur}/{whSmallMax} boxes" +
                        (whUnitsMax > 0 ? $"  => {whUnitsCur}/{whUnitsMax} units" : "")
                    );
                    sb.AppendLine(
                        $"  LOOSE     : {looseUnitsCur} / {looseUnitsMax} units " +
                        $"(boxes={looseT.Boxes}, big={looseT.BigBoxes}, small={looseT.SmallBoxes})"
                    );

                    if (whUnitsMax > 0 || looseUnitsMax > 0 || shopMax > 0)
                        sb.AppendLine($"  TOTAL     : {totalUnitsCur} / {totalUnitsMax} units");

                    sb.AppendLine();
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                LogInfo($"[WarehouseManager] Combined scan complete. Items={allKeys.Count}. File: {path}");
            }
            catch (Exception ex)
            {
                LogError($"[WarehouseManager] Combined scan FAILED: {ex}");
            }
        }

        // ---- Logging wrappers (BepInEx + WM file log) ----
        private static void LogInfo(string msg)
        {
            Plugin.Logger.LogInfo(msg);
            WMFileLogger.Info(msg);
        }

        private static void LogError(string msg)
        {
            Plugin.Logger.LogError(msg);
            WMFileLogger.Error(msg);
        }
    }
}
