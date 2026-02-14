#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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

        internal static Dictionary<EItemType, WarehouseTotals> Scan()
        {
            var dict = new Dictionary<EItemType, WarehouseTotals>();

            // Find all ShelfCompartment MBs by type name (works even if type is non-public)
            var compartments = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(mb => mb != null && mb.GetType().Name == "ShelfCompartment")
                .ToList();

            foreach (var mb in compartments)
            {
                if (mb == null) continue;

                // Warehouse box-slots are the ones that can accept boxes
                if (!TryReadBool(mb, "m_CanPutBox", "canPutBox", out bool canPutBox) || !canPutBox)
                    continue;

                if (!TryReadItemType(mb, out var itemType))
                    continue;

                if (!TryReadInt(mb, "m_ItemAmount", "itemAmount", out int curBoxes))
                    continue;

                // Some slots report max=0 when empty; we recover max as 1 for box-slot style compartments.
                // (Most warehouse compartments are effectively 1 box per slot.)
                int maxBoxes = 1;
                if (TryReadInt(mb, "m_MaxItemCount", "maxItemCount", out int rawMax) && rawMax > 0)
                    maxBoxes = rawMax;

                // infer big vs small by UPB max-per-box (PackagingInfo knows)
                var pack = PackagingInfo.Get(itemType);

                bool isBig =
                    pack.UpbBig > 0 &&
                    (pack.UpbSmall <= 0 || pack.UpbBig >= pack.UpbSmall);

                // If both exist, try to classify by “max per slot” heuristic:
                // If game uses big boxes in warehouse for this item, treat as big; otherwise small.
                // (We can't read the physical box size from compartment reliably, so we use packaging defaults.)
                if (!dict.TryGetValue(itemType, out var t))
                    t = new WarehouseTotals();

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

            // m_ItemType is usually EItemType. Sometimes it might be string; we try both.
            if (TryGetMemberValue(mb, "m_ItemType", "itemType", out object? v) && v != null)
            {
                if (v is EItemType eit)
                {
                    itemType = eit;
                    return true;
                }

                // string fallback
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
