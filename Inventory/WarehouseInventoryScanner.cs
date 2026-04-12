#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace WarehouseManager.Inventory
{
    internal static class WarehouseInventoryScanner
    {
        internal struct WarehouseTotals
        {
            public int BoxesCurBig;
            public int BoxesMaxBig;
            public int BoxesCurSmall;
            public int BoxesMaxSmall;
        }

        private static MonoBehaviour[]? _cachedShelfCompartmentMBs;
        private static float _nextRefreshRealtime;
        private const float RefreshIntervalSeconds = 30f;

        internal static void InvalidateCache()
        {
            _cachedShelfCompartmentMBs = null;
            _nextRefreshRealtime = 0f;
        }

        private static MonoBehaviour[] GetShelfCompartmentMBs()
        {
            float now = Time.realtimeSinceStartup;

            if (_cachedShelfCompartmentMBs != null && now < _nextRefreshRealtime)
                return _cachedShelfCompartmentMBs;

            var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();

            // First pass count
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var mb = all[i];
                if (mb == null) continue;
                if (mb.GetType().Name == "ShelfCompartment")
                    count++;
            }

            var arr = new MonoBehaviour[count];
            int w = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var mb = all[i];
                if (mb == null) continue;
                if (mb.GetType().Name == "ShelfCompartment")
                    arr[w++] = mb;
            }

            _cachedShelfCompartmentMBs = arr;
            _nextRefreshRealtime = now + RefreshIntervalSeconds;
            return arr;
        }

        internal static Dictionary<EItemType, WarehouseTotals> Scan()
        {
            var dict = new Dictionary<EItemType, WarehouseTotals>();

            var compartments = GetShelfCompartmentMBs();

            for (int i = 0; i < compartments.Length; i++)
            {
                var mb = compartments[i];
                if (mb == null) continue;

                // Warehouse box-slots are the ones that can accept boxes
                if (!TryReadBool(mb, "m_CanPutBox", "canPutBox", out bool canPutBox) || !canPutBox)
                    continue;

                if (!TryReadItemType(mb, out var itemType))
                    continue;

                if (!TryReadInt(mb, "m_ItemAmount", "itemAmount", out int curBoxes))
                    continue;

                int maxBoxes = 1;
                if (TryReadInt(mb, "m_MaxItemCount", "maxItemCount", out int rawMax) && rawMax > 0)
                    maxBoxes = rawMax;

                if (!TryReadBool(mb, "m_IsBigBox", "isBigBox", out bool isBig))
                    continue;

                if (!dict.TryGetValue(itemType, out var t))
                    t = default;

                if (isBig)
                {
                    t.BoxesCurBig += Math.Max(0, curBoxes);
                    t.BoxesMaxBig += Math.Max(0, maxBoxes);
                }
                else
                {
                    t.BoxesCurSmall += Math.Max(0, curBoxes);
                    t.BoxesMaxSmall += Math.Max(0, maxBoxes);
                }

                dict[itemType] = t;
            }

            return dict;
        }

        // =========================
        // Reflection readers
        // =========================

        private static bool TryReadItemType(MonoBehaviour mb, out EItemType itemType)
        {
            itemType = default;

            if (TryGetMemberValue(mb, "m_ItemType", "itemType", out object? v) && v != null)
            {
                if (v is EItemType eit)
                {
                    itemType = eit;
                    return true;
                }

                if (Enum.TryParse(typeof(EItemType), v.ToString(), true, out var parsed) && parsed is EItemType e2)
                {
                    itemType = e2;
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadInt(MonoBehaviour mb, string a, string b, out int value)
        {
            value = 0;
            if (!TryGetMemberValue(mb, a, b, out object? v) || v == null) return false;

            if (v is int i) { value = i; return true; }
            if (v is long l) { value = (int)l; return true; }
            if (v is double d) { value = (int)d; return true; }

            return int.TryParse(v.ToString(), out value);
        }

        private static bool TryReadBool(MonoBehaviour mb, string a, string b, out bool value)
        {
            value = false;
            if (!TryGetMemberValue(mb, a, b, out object? v) || v == null) return false;

            if (v is bool bo) { value = bo; return true; }
            return bool.TryParse(v.ToString(), out value);
        }

        private static bool TryGetMemberValue(object obj, string nameA, string nameB, out object? value)
        {
            value = null;
            var t = obj.GetType();
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var f = t.GetField(nameA, BF) ?? t.GetField(nameB, BF);
            if (f != null) { value = f.GetValue(obj); return true; }

            var p = t.GetProperty(nameA, BF) ?? t.GetProperty(nameB, BF);
            if (p != null && p.CanRead)
            {
                try { value = p.GetValue(obj, null); return true; }
                catch { return false; }
            }

            return false;
        }
    }
}