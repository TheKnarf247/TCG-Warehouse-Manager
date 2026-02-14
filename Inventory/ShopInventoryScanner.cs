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

        internal static Dictionary<EItemType, InventoryTotals> Scan()
        {
            var result = new Dictionary<EItemType, InventoryTotals>();

            var shelves = Resources.FindObjectsOfTypeAll<Shelf>();

            foreach (var shelf in shelves)
            {
                if (shelf == null)
                    continue;

                // Match OCR behavior
                try
                {
                    if (!shelf.IsValidObject())
                        continue;

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

                foreach (var c in comps)
                {
                    if (c == null)
                        continue;

                    var itemType = c.GetItemType();
                    if (itemType == EItemType.None)
                        continue;

                    int cur = Mathf.Max(0, c.GetItemCount());
                    int max = Mathf.Max(0, c.GetMaxItemCount());
                    if (max <= 0)
                        continue;

                    if (!result.TryGetValue(itemType, out var totals))
                        totals = new InventoryTotals();

                    totals.Current += cur;
                    totals.Max += max;
                    result[itemType] = totals;
                }
            }

            return result;
        }
    }
}
