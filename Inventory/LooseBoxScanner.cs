#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace WarehouseManager.Inventory
{
    internal static class LooseBoxScanner
    {
        internal struct LooseTotals
        {
            public int Boxes;        // count of physical boxes
            public int UnitsCur;     // items currently in those boxes
            public int UnitsMax;     // capacity of those boxes
            public int BigBoxes;     // count big boxes
            public int SmallBoxes;   // count small boxes
        }

        internal static Dictionary<EItemType, LooseTotals> Scan()
        {
            var result = new Dictionary<EItemType, LooseTotals>();

            // OCR uses this to find packaging boxes with items inside
            foreach (var box in RestockManager.GetItemPackagingBoxListWithItem(true))
            {
                if (box == null)
                    continue;

                // We only want loose / carried / floor etc.
                // Stored ones are already accounted for in Warehouse shelves.
                if (box.m_IsStored)
                    continue;

                var itemType = box.GetItemType();
                if (itemType == EItemType.None)
                    continue;

                int unitsCur = 0;
                int unitsMax = 0;

                try
                {
                    if (box.m_ItemCompartment != null)
                    {
                        unitsCur = Mathf.Max(0, box.m_ItemCompartment.GetItemCount());
                        unitsMax = Mathf.Max(0, box.m_ItemCompartment.GetMaxItemCount());
                    }
                }
                catch
                {
                    // If compartment is weird, still count the box
                }

                if (!result.TryGetValue(itemType, out var t))
                    t = new LooseTotals();

                t.Boxes += 1;
                t.UnitsCur += unitsCur;
                t.UnitsMax += unitsMax;

                if (box.m_IsBigBox) t.BigBoxes += 1;
                else t.SmallBoxes += 1;

                result[itemType] = t;
            }

            return result;
        }
    }
}
