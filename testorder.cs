#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using WarehouseManager.Inventory;

namespace WarehouseManager
{
    internal static class TestOrder
    {
        private static float _lastPressTime;
        private const float DebounceSeconds = 0.50f;

        private static readonly KeyCode MainKey = KeyCode.F8;

        internal static void Update()
        {
            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!shiftHeld) return;

            if (!Input.GetKeyDown(MainKey)) return;

            if (Time.realtimeSinceStartup - _lastPressTime < DebounceSeconds) return;
            _lastPressTime = Time.realtimeSinceStartup;

            LogInfo("[WarehouseManager] Shift+F8 detected. Running TEST order (1 box) from shelf scan...");
            RunTestOrder_FirstNeededItem();
        }

        private static void RunTestOrder_FirstNeededItem()
        {
            try
            {
                var screen = FindScannerScreenInstance();
                if (screen == null)
                {
                    LogWarn("[TEST] ScannerRestockScreen not found. Open the scanner UI once, then try again.");
                    return;
                }

                // Scans (EItemType keyed)
                var wh = WarehouseInventoryScanner.Scan();
                var sh = ShopInventoryScanner.Scan();
                var loose = LooseBoxScanner.Scan();

                // Union of keys
                var all = new HashSet<EItemType>();
                foreach (var k in wh.Keys) all.Add(k);
                foreach (var k in sh.Keys) all.Add(k);
                foreach (var k in loose.Keys) all.Add(k);

                if (all.Count == 0)
                {
                    LogWarn("[TEST] No inventory items detected by scanners.");
                    return;
                }

                // InventoryBase restock list lookup
                if (!TryGetRestockList(out IList restockList))
                {
                    LogError("[TEST] Could not access InventoryBase.m_StockItemData_SO.m_RestockDataList.");
                    return;
                }

                // Find first "needs order" item by name order (stable test)
                foreach (var itemType in all.OrderBy(t => t.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    if (itemType == EItemType.None) continue;

                    sh.TryGetValue(itemType, out var shopT);
                    wh.TryGetValue(itemType, out var whT);
                    loose.TryGetValue(itemType, out var looseT);

                    // Need restockIndex + UPB from InventoryBase
                    if (!TryFindRestockIndexAndUpb(restockList, itemType, out int idx, out int upb))
                        continue;

                    if (upb <= 0) continue;

                    // ✅ Warehouse scanner totals: support both "Current/Max" and "BoxesCurBig/Small" shapes
                    int whBoxesCur = GetWarehouseBoxesCur(whT);
                    int whBoxesMax = GetWarehouseBoxesMax(whT);

                    int shopCur = GetIntFromTotals(shopT, "Current", "Cur", "UnitsCur");
                    int shopMax = GetIntFromTotals(shopT, "Max", "UnitsMax");

                    // ✅ Loose counts toward CURRENT only, NOT MAX
                    int looseCurUnits = GetIntFromTotals(looseT, "UnitsCur", "Current", "Cur");

                    int whUnitsCur = whBoxesCur * upb;
                    int whUnitsMax = whBoxesMax * upb;

                    int currentUnits = shopCur + whUnitsCur + looseCurUnits;
                    int capacityUnits = shopMax + whUnitsMax;

                    int needUnits = capacityUnits - currentUnits;
                    if (needUnits <= 0) continue;

                    // if no free box slots, ordering makes no sense for your model
                    int whFreeBoxes = Mathf.Max(0, whBoxesMax - whBoxesCur);
                    if (whFreeBoxes <= 0)
                    {
                        LogInfo($"[TEST] {itemType} needs units but WH has no free box slots. Skipping.");
                        continue;
                    }

                    LogInfo(
                        $"[TEST] Ordering 1 box: itemType={itemType} idx={idx} upb={upb} " +
                        $"needUnits={needUnits} (capacity={capacityUnits}, current={currentUnits}, looseCur={looseCurUnits}) " +
                        $"whBoxes={whBoxesCur}/{whBoxesMax}"
                    );

                    InvokeScanner(screen, "AddToCartForCheckout", new object[] { idx, 1 });
                    InvokeScanner(screen, "UpdateTotalCostAndBoxCount", null);
                    InvokeScanner(screen, "OnPressCheckoutButton", null);

                    LogInfo("[TEST] Checkout invoked.");
                    return;
                }

                LogWarn("[TEST] No candidate item found that needs ordering (or no restockIndex/UPB match).");
            }
            catch (Exception ex)
            {
                LogError($"[TEST] Test order failed: {ex}");
            }
        }

        // =========================
        // Warehouse totals adapter
        // =========================

        private static int GetWarehouseBoxesCur(object whTotals)
        {
            // Preferred: split-by-box-type struct
            int big = GetIntFromTotals(whTotals, "BoxesCurBig", "BigCur", "CurBig");
            int small = GetIntFromTotals(whTotals, "BoxesCurSmall", "SmallCur", "CurSmall");

            if (big != 0 || small != 0)
                return Mathf.Max(0, big + small);

            // Fallback: simple Current
            return Mathf.Max(0, GetIntFromTotals(whTotals, "Current", "BoxesCur", "Cur"));
        }

        private static int GetWarehouseBoxesMax(object whTotals)
        {
            int big = GetIntFromTotals(whTotals, "BoxesMaxBig", "BigMax", "MaxBig");
            int small = GetIntFromTotals(whTotals, "BoxesMaxSmall", "SmallMax", "MaxSmall");

            if (big != 0 || small != 0)
                return Mathf.Max(0, big + small);

            return Mathf.Max(0, GetIntFromTotals(whTotals, "Max", "BoxesMax"));
        }

        /// <summary>
        /// Reads an int from a totals struct/class by trying multiple field/property names.
        /// Works on structs (boxed) and classes.
        /// </summary>
        private static int GetIntFromTotals(object totals, params string[] names)
        {
            try
            {
                var t = totals.GetType();
                const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                foreach (var n in names)
                {
                    var f = t.GetField(n, BF);
                    if (f != null) return CoerceInt(f.GetValue(totals));

                    var p = t.GetProperty(n, BF);
                    if (p != null && p.CanRead) return CoerceInt(p.GetValue(totals, null));
                }
            }
            catch { }

            return 0;
        }

        // =========================
        // Scanner helpers
        // =========================

        private static Component? FindScannerScreenInstance()
        {
            var t = FindTypeByName("ScannerRestockScreen");
            if (t == null) return null;

            var found = Resources.FindObjectsOfTypeAll(t);
            if (found == null || found.Length == 0) return null;

            foreach (var o in found)
                if (o is Component c) return c;

            return found[0] as Component;
        }

        private static void InvokeScanner(Component screen, string methodName, object[]? args)
        {
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var m = screen.GetType().GetMethod(methodName, BF);
            if (m == null) throw new MissingMethodException(screen.GetType().FullName, methodName);
            m.Invoke(screen, args);
        }

        // =========================
        // InventoryBase restock list helpers
        // =========================

        private static bool TryGetRestockList(out IList list)
        {
            list = null!;

            try
            {
                object? invBase = GetSingletonInstanceByName("InventoryBase");
                if (invBase == null) return false;

                object? stockSO = GetFieldOrProperty(invBase, "m_StockItemData_SO");
                if (stockSO == null) return false;

                object? listObj = GetFieldOrProperty(stockSO, "m_RestockDataList");
                if (listObj is not IList il || il.Count == 0) return false;

                list = il;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindRestockIndexAndUpb(IList restockList, EItemType itemType, out int restockIndex, out int upb)
        {
            restockIndex = -1;
            upb = 0;

            try
            {
                for (int i = 0; i < restockList.Count; i++)
                {
                    var rd = restockList[i];
                    if (rd == null) continue;

                    object? it = GetFieldOrProperty(rd, "itemType") ?? GetFieldOrProperty(rd, "m_ItemType");
                    if (it == null) continue;

                    if (!TryCoerceEItemType(it, out var rdType)) continue;
                    if (rdType != itemType) continue;

                    // TEMP: remove license gate to prove whether it's blocking EPL
                    // if (!IsLicenseUnlockedBestEffort(i))
                    //     continue;

                    int amount = CoerceInt(GetFieldOrProperty(rd, "amount") ?? GetFieldOrProperty(rd, "m_Amount"));
                    if (amount <= 0) continue;

                    bool isBigBox = false;
                    var bbObj = GetFieldOrProperty(rd, "isBigBox") ?? GetFieldOrProperty(rd, "m_IsBigBox");
                    if (bbObj is bool b) isBigBox = b;

                    string name = (GetFieldOrProperty(rd, "name") ?? GetFieldOrProperty(rd, "m_Name"))?.ToString() ?? "(no name)";

                    LogInfo($"[TEST] Candidate match: itemType={itemType} restockIndex={i} amount(upb)={amount} isBigBox={isBigBox} name={name}");

                    restockIndex = i;
                    upb = amount;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCoerceEItemType(object v, out EItemType itemType)
        {
            itemType = EItemType.None;

            try
            {
                if (v is EItemType eit) { itemType = eit; return true; }
                if (v is int i) { itemType = (EItemType)i; return true; }

                var s = v.ToString();
                if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse<EItemType>(s, true, out var parsed))
                {
                    itemType = parsed;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsLicenseUnlockedBestEffort(int restockIndex)
        {
            try
            {
                var t = FindTypeByName("CPlayerData");
                if (t == null) return true;

                const BindingFlags BF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var m = t.GetMethod("GetIsItemLicenseUnlocked", BF, null, new[] { typeof(int) }, null);
                if (m == null) return true;

                var res = m.Invoke(null, new object[] { restockIndex });
                if (res is bool b) return b;

                return true;
            }
            catch
            {
                return true;
            }
        }

        // =========================
        // Generic reflection helpers
        // =========================

        private static object? GetFieldOrProperty(object obj, string name)
        {
            var t = obj.GetType();
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var f = t.GetField(name, BF);
            if (f != null) return f.GetValue(obj);

            var p = t.GetProperty(name, BF);
            if (p != null && p.CanRead)
            {
                try { return p.GetValue(obj, null); }
                catch { return null; }
            }

            return null;
        }

        private static int CoerceInt(object? v)
        {
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            if (v == null) return 0;
            int.TryParse(v.ToString(), out int parsed);
            return parsed;
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

        private static object? GetSingletonInstanceByName(string typeName)
        {
            Type? targetType = FindTypeByName(typeName);
            if (targetType == null) return null;

            Type? csingletonOpen = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.IsGenericTypeDefinition && t.Name.StartsWith("CSingleton", StringComparison.Ordinal))
                    {
                        csingletonOpen = t;
                        break;
                    }
                }
                if (csingletonOpen != null) break;
            }

            if (csingletonOpen == null) return null;

            try
            {
                var csingletonClosed = csingletonOpen.MakeGenericType(targetType);
                var instProp = csingletonClosed.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                return instProp?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        // =========================
        // Logging (works with either Plugin.Logger or Plugin.Log)
        // =========================

        private static void LogInfo(string msg) => LogViaPlugin("LogInfo", msg);
        private static void LogWarn(string msg) => LogViaPlugin("LogWarning", msg);
        private static void LogError(string msg) => LogViaPlugin("LogError", msg);

        private static void LogViaPlugin(string method, string msg)
        {
            try
            {
                var pt = typeof(Plugin);

                var prop = pt.GetProperty("Logger", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var loggerObj = prop?.GetValue(null);

                if (loggerObj == null)
                {
                    var field = pt.GetField("Log", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    loggerObj = field?.GetValue(null);
                }

                if (loggerObj != null)
                {
                    var m = loggerObj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
                    if (m != null)
                    {
                        m.Invoke(loggerObj, new object[] { msg });
                        return;
                    }
                }
            }
            catch { }

            Debug.Log(msg);
        }
    }
}
