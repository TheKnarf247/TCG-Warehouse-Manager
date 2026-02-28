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

                for (int i = 0; i < boxes.Count; i++)
                {
                    var box = boxes[i];
                    if (box == null) { skipped++; continue; }

                    if (TryMoveBox(box))
                        moved++;
                    else
                        skipped++;
                }

                Plugin.LogInfo($"[Stocker] {reasonTag}: moved={moved}, skipped={skipped}");
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
                    if (shelfComp == null) continue;

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

        private static bool TryMoveBox(InteractablePackagingBox_Item box)
        {
            if (box.m_IsStored) return false;
            if (box.IsBoxOpened()) return false;

            if (box.m_Collider != null && !box.m_Collider.enabled) return false;

            var itemType = box.GetItemType();
            if (itemType == EItemType.None)
                return false;

            var count = box.m_ItemCompartment != null ? box.m_ItemCompartment.GetItemCount() : 0;
            if (count <= 0) return false;

            if (!_targetsByItemType.TryGetValue(itemType, out var candidates) || candidates.Count == 0)
                return false;

            bool isBigBox = box.m_IsBigBox;

            for (int i = 0; i < candidates.Count; i++)
            {
                var target = candidates[i];
                if (target == null) continue;

                var existing = target.GetInteractablePackagingBoxList();
                if (existing != null && existing.Count > 0)
                {
                    var last = target.GetLastInteractablePackagingBox();
                    if (last == null) continue;

                    if (last.m_IsBigBox != isBigBox)
                        continue;

                    if (!target.HasEnoughSlot())
                        continue;
                }

                box.SetPhysicsEnabled(false);

                var t = target.GetEmptySlotTransform();
                if (t != null)
                {
                    box.transform.position = t.position;
                    box.transform.rotation = t.rotation;
                }

                box.DispenseItem(false, target);
                target.SetPriceTagItemAmountText();
                return true;
            }

            return false;
        }
    }
}