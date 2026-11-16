#nullable enable
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WarehouseManager.Config;

namespace WarehouseManager.Automation
{
    internal static class Stocker
    {
        private static bool _isRunning;

        // One reason per skipped physical box; reported once per Stocker run.
        private enum SkipReason
        {
            InvalidOrUnavailable,
            NoAssignedShelf,
            NoFreeSlot,
            WrongBoxSize,
            NoSlotTransform
        }


        // Faster: use EItemType as key (no ToString allocations, no string comparisons)
        private static readonly Dictionary<EItemType, List<ShelfCompartment>> _targetsByItemType = new();

        // Fast private-field access (Harmony FieldRef) for ShelfManager.m_WarehouseShelfList
        private static AccessTools.FieldRef<ShelfManager, List<WarehouseShelf>>? _warehouseShelfListRef;
        private static bool _fieldRefTried;

        internal static void TryRun(string reasonTag)
        {
            if (!WMConfig.StockerEnabled.Value) return;
            RunOnce(reasonTag);
        }

        internal static void RunOnce(string reasonTag)
        {
            if (!WMConfig.StockerEnabled.Value) return;
            if (_isRunning) return;

            _isRunning = true;
            try
            {
                EnsureFieldRef();

                var shelfManager = UnityEngine.Object.FindObjectOfType<ShelfManager>();
                if (shelfManager == null)
                {
                    Plugin.LogWarn($"[Stocker] ShelfManager not found. ({reasonTag})");
                    return;
                }

                BuildWarehouseTargetCache(shelfManager);

                // FIX: your API has no includeStored parameter
                var boxes = RestockManager.GetItemPackagingBoxListWithItem(true);
                if (boxes == null || boxes.Count == 0)
                {
                    Plugin.LogVerbose($"[Stocker] No packaging boxes found. ({reasonTag})");
                    return;
                }

                int moved = 0;
                int skipped = 0;
                int invalid = 0;
                int noTarget = 0;
                int full = 0;
                int wrongSize = 0;
                int noTransform = 0;

                for (int i = 0; i < boxes.Count; i++)
                {
                    var box = boxes[i];
                    SkipReason why;
                    if (TryMoveBox(box, out why))
                    {
                        moved++;
                        continue;
                    }

                    skipped++;
                    switch (why)
                    {
                        case SkipReason.NoAssignedShelf: noTarget++; break;
                        case SkipReason.NoFreeSlot: full++; break;
                        case SkipReason.WrongBoxSize: wrongSize++; break;
                        case SkipReason.NoSlotTransform: noTransform++; break;
                        default: invalid++; break;
                    }
                }

                Plugin.LogInfo($"[Stocker] {reasonTag}: moved={moved}, skipped={skipped} " +
                    $"(invalid/held={invalid}, noTarget={noTarget}, full={full}, wrongSize={wrongSize}, noTransform={noTransform})");
            }
            catch (Exception e)
            {
                Plugin.LogError($"[Stocker] {reasonTag} failed: {e}");
            }
            finally
            {
                _isRunning = false;
            }
        }

        private static void EnsureFieldRef()
        {
            if (_fieldRefTried) return;
            _fieldRefTried = true;

            try
            {
                _warehouseShelfListRef = AccessTools.FieldRefAccess<ShelfManager, List<WarehouseShelf>>("m_WarehouseShelfList");
                Plugin.LogDebug("[Stocker] Bound fast field ref for ShelfManager.m_WarehouseShelfList.");
            }
            catch (Exception e)
            {
                _warehouseShelfListRef = null;
                Plugin.LogWarn($"[Stocker] Could not bind field ref for m_WarehouseShelfList (fallback will be used): {e.Message}");
            }
        }

        private static void BuildWarehouseTargetCache(ShelfManager shelfManager)
        {
            _targetsByItemType.Clear();

            List<WarehouseShelf>? shelves = null;

            // Prefer fast fieldref accessor
            try
            {
                if (_warehouseShelfListRef != null)
                    shelves = _warehouseShelfListRef(shelfManager);
            }
            catch
            {
                shelves = null;
            }

            // Fallback (only if fieldref failed)
            shelves ??= Traverse.Create(shelfManager).Field<List<WarehouseShelf>>("m_WarehouseShelfList").Value;

            if (shelves == null || shelves.Count == 0)
            {
                Plugin.LogWarn("[Stocker] Warehouse shelf list empty/unavailable.");
                return;
            }

            for (int shelfIndex = 0; shelfIndex < shelves.Count; shelfIndex++)
            {
                var whShelf = shelves[shelfIndex];
                if (whShelf == null) continue;

                if (whShelf.GetIsBoxedUp())
                    continue;

                var storageComps = whShelf.GetStorageCompartmentList();
                if (storageComps == null) continue;

                for (int compIndex = 0; compIndex < storageComps.Count; compIndex++)
                {
                    var storage = storageComps[compIndex];
                    if (storage == null) continue;

                    var shelfComp = storage.GetShelfCompartment();
                    if (shelfComp == null || !shelfComp.m_CanPutBox) continue;

                    var itemType = shelfComp.GetItemType();

                    // Skip untagged/None
                    if (itemType == EItemType.None)
                        continue;

                    if (!_targetsByItemType.TryGetValue(itemType, out var list))
                    {
                        list = new List<ShelfCompartment>(4);
                        _targetsByItemType[itemType] = list;
                    }

                    list.Add(shelfComp);
                }
            }
        }

        private static bool TryMoveBox(InteractablePackagingBox_Item box, out SkipReason reason)
        {
            reason = SkipReason.InvalidOrUnavailable;

            if (box == null || box.m_IsStored || box.m_IsBeingHold || box.IsBoxOpened())
                return false;

            if (box.m_Collider != null && !box.m_Collider.enabled)
                return false;

            var itemType = box.GetItemType();
            if (itemType == EItemType.None || box.m_ItemCompartment == null ||
                box.m_ItemCompartment.GetItemCount() <= 0)
                return false;

            if (!_targetsByItemType.TryGetValue(itemType, out var candidates) || candidates.Count == 0)
            {
                reason = SkipReason.NoAssignedShelf;
                return false;
            }

            bool isBigBox = box.m_IsBigBox;
            bool sawFull = false;
            bool sawSizeMismatch = false;
            bool sawMissingTransform = false;

            for (int i = 0; i < candidates.Count; i++)
            {
                var target = candidates[i];
                if (target == null || !target.m_CanPutBox || target.GetItemType() != itemType)
                    continue;

                // The new game stores boxes as StoredBoxRecord. Its physical-box
                // list can be empty even when the compartment is completely full.
                int occupied = target.GetItemCount();
                int storedRecords = target.GetStoredBoxRecordCount();
                var physical = target.GetInteractablePackagingBoxList();

                if (occupied > 0 || storedRecords > 0 || (physical != null && physical.Count > 0))
                {
                    if (target.GetIsBigBox() != isBigBox)
                    {
                        sawSizeMismatch = true;
                        continue;
                    }

                    if (storedRecords > 0 && target.PeekStoredBoxRecord(0).isBigBox != isBigBox)
                    {
                        sawSizeMismatch = true;
                        continue;
                    }
                }
                else
                {
                    // Updates the capacity/position layout for the incoming box size.
                    // Only do this for an empty compartment.
                    target.CheckBoxType(isBigBox);
                }

                // Must be checked unconditionally: GetEmptySlotTransform clamps
                // an out-of-range index onto the LAST slot of a full shelf.
                int capacity = target.GetMaxItemCount();
                if (capacity <= 0 || occupied >= capacity || !target.HasEnoughSlot())
                {
                    sawFull = true;
                    continue;
                }

                var slot = target.GetEmptySlotTransform();
                if (slot == null)
                {
                    sawMissingTransform = true;
                    continue;
                }

                // Leave the existing game's deposit call untouched until we
                // inspect InteractablePackagingBox_Item.DispenseItem implementation.
                box.SetPhysicsEnabled(false);
                box.transform.position = slot.position;
                box.transform.rotation = slot.rotation;
                box.DispenseItem(false, target);
                target.SetPriceTagItemAmountText();
                return true;
            }

            reason = sawFull ? SkipReason.NoFreeSlot :
                     sawSizeMismatch ? SkipReason.WrongBoxSize :
                     sawMissingTransform ? SkipReason.NoSlotTransform :
                     SkipReason.NoAssignedShelf;
            return false;
        }
    }
}
