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
            var seen = new HashSet<int>(); // prevent double-counting same box

            void AddBox(InteractablePackagingBox_Item box)
            {
                if (box == null)
                    return;

                int id = box.GetInstanceID();
                if (!seen.Add(id))
                    return;

                // Only loose (not stored)
                if (box.m_IsStored)
                    return;

                var itemType = box.GetItemType();
                if (itemType == EItemType.None)
                    return;

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
                    // still count the box if compartment is weird
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

            // ------------------------------------------------------------
            // 1) Loose boxes on the floor / world (game-provided list)
            // ------------------------------------------------------------
            foreach (var box in RestockManager.GetItemPackagingBoxListWithItem(true))
            {
                if (box == null) continue;
                // AddBox will ignore stored + dedupe
                AddBox(box);
            }

            // ------------------------------------------------------------
            // 2) Held boxes (player or workers) that the list MISSES
            //    Use m_IsBeingHold from InteractableObject base.
            // ------------------------------------------------------------
            try
            {
                var all = Resources.FindObjectsOfTypeAll<InteractablePackagingBox_Item>();
                foreach (var box in all)
                {
                    if (box == null) continue;
                    if (box.m_IsStored) continue;

                    // This is the key fix:
                    // if it is being held, count it as loose too (reserves a WH slot)
                    if (box.m_IsBeingHold)
                        AddBox(box);
                }
            }
            catch
            {
                // don't break ordering if Unity/Resources behaves oddly
            }

            return result;
        }
    }
}
