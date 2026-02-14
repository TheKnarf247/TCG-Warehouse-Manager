#nullable enable

using System;
using System.Collections.Generic;
using WarehouseManager.Inventory;

namespace WarehouseManager.Ordering
{
    internal static class OrderCalculator
    {
        internal sealed class OrderDecision
        {
            public EItemType ItemType;
            public int BoxesToOrder;
            public int UnitsPerBox;
        }

        internal static List<OrderDecision> Calculate(
            Dictionary<EItemType, ShopInventoryScanner.InventoryTotals> shop,
            Dictionary<EItemType, WarehouseInventoryScanner.WarehouseTotals> warehouse,
            Dictionary<EItemType, LooseBoxScanner.LooseTotals> loose)
        {
            var results = new List<OrderDecision>();

            var allKeys = new HashSet<EItemType>();
            foreach (var k in shop.Keys) allKeys.Add(k);
            foreach (var k in warehouse.Keys) allKeys.Add(k);
            foreach (var k in loose.Keys) allKeys.Add(k);

            foreach (var item in allKeys)
            {
                shop.TryGetValue(item, out var sh);
                warehouse.TryGetValue(item, out var wh);
                loose.TryGetValue(item, out var lo);

                var pack = PackagingInfo.Get(item);

                // 🔒 BIG ONLY policy
                if (pack.UpbBig <= 0)
                    continue;

                int unitsPerBox = pack.UpbBig;

                int shopCur = sh.Current;
                int shopMax = sh.Max;

                int whUnitsCur =
                    (wh.BoxesCurBig * pack.UpbBig) +
                    (wh.BoxesCurSmall * pack.UpbSmall);

                int whUnitsMax =
                    (wh.BoxesMaxBig * pack.UpbBig) +
                    (wh.BoxesMaxSmall * pack.UpbSmall);

                int looseUnits = lo.UnitsCur;

                // ✔ Loose counts toward ON HAND
                int totalOnHand = shopCur + whUnitsCur + looseUnits;

                // ✔ Loose does NOT increase capacity
                int totalMax = shopMax + whUnitsMax;

                if (totalOnHand >= totalMax)
                    continue;

                int deficit = totalMax - totalOnHand;

                // Floor division ensures we never exceed capacity
                int boxesToOrder = deficit / unitsPerBox;

                if (boxesToOrder <= 0)
                    continue;

                // Final safety check
                if (totalOnHand + (boxesToOrder * unitsPerBox) > totalMax)
                    continue;

                results.Add(new OrderDecision
                {
                    ItemType = item,
                    BoxesToOrder = boxesToOrder,
                    UnitsPerBox = unitsPerBox
                });
            }

            return results;
        }
    }
}
