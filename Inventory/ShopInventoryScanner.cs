#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace WarehouseManager.Inventory
{
    internal static class ShopInventoryScanner
    {
        internal struct InventoryTotals
        {
            public int Current;
            public int Max;
        }

        private static Shelf[]? _cachedShelves;
        private static float _nextRefreshRealtime;

        // Refresh at most every N seconds (tune)
        private const float RefreshIntervalSeconds = 30f;

        internal static void InvalidateCache()
        {
            _cachedShelves = null;
            _nextRefreshRealtime = 0f;
        }

        private static Shelf[] GetShelves()
        {
            float now = Time.realtimeSinceStartup;

            if (_cachedShelves == null || now >= _nextRefreshRealtime)
            {
                _cachedShelves = Resources.FindObjectsOfTypeAll<Shelf>();
                _nextRefreshRealtime = now + RefreshIntervalSeconds;
            }

            return _cachedShelves;
        }

        internal static Dictionary<EItemType, InventoryTotals> Scan()
        {
            var result = new Dictionary<EItemType, InventoryTotals>();

            var shelves = GetShelves();

            for (int s = 0; s < shelves.Length; s++)
            {
                var shelf = shelves[s];
                if (shelf == null)
                    continue;

                // Match OCR behavior
                try
                {
                    if (!shelf.IsValidObject())
                        continue;

                    // This allocates; if you know "Personal" maps to a constant/enum, we can optimize later.
                    if (shelf.m_ObjectType.ToString().StartsWith("Personal"))
                        continue;
                }
                catch
                {
                    continue;
                }

                var comps = shelf.GetItemCompartmentList();
                if (comps == null)
                    continue;

                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (c == null)
                        continue;

                    var itemType = c.GetItemType();
                    if (itemType == EItemType.None)
                        continue;

                    int cur = c.GetItemCount();
                    if (cur < 0) cur = 0;

                    int max = c.GetMaxItemCount();
                    if (max <= 0) continue;

                    if (!result.TryGetValue(itemType, out var totals))
                        totals = default;

                    totals.Current += cur;
                    totals.Max += max;
                    result[itemType] = totals;
                }
            }

            return result;
        }
    }
}