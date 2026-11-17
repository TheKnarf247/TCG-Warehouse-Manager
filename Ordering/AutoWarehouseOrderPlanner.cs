#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using Knarf.AwCompat;

namespace WarehouseManager.Ordering
{
    internal static class AutoWarehouseOrderPlanner
    {
        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<int>? _defaultMinimum;
        private static ConfigEntry<string>? _overrides;
        private static ConfigEntry<string>? _diagnosticItem;
        private static string? _lastError;

        internal static void Bind(ConfigFile cfg)
        {
            const string section = "03b - Auto Warehouse";
            _enabled = cfg.Bind(section, "Enabled", false,
                "Use Auto Warehouse storage for WM orders instead of physical shelf capacity. Requires AW 2.0.2 and DirectPurchaseDelivery. Turn AW Auto Order and WM Stocker off.");
            _defaultMinimum = cfg.Bind(section, "MinimumBoxesInStorage", 2,
                new ConfigDescription("Target reserve per tracked product (0 disables it). Reorder below (minimum - 1) full boxes, buying as many whole boxes as fit without exceeding the target. Minimum 1 reorders at zero.",
                    new AcceptableValueRange<int>(0, 5000)));
            _overrides = cfg.Bind(section, "MinimumBoxesPerItem", "",
                "Optional per-product overrides: EItemType=3,OtherItemType=0. Separate entries with commas. Use internal item keys from the WM catalog. 0 skips that item.");
            _diagnosticItem = cfg.Bind(section, "DiagnosticItem", "RareCardBox",
                "Internal item key to trace during reserve planning. Blank disables the per-product diagnostic.");
        }

        // True means the AW mode owns this planning attempt, even when no order is safe.
        // False means keep the existing physical-shelf planner (AW absent or not opted in).
        internal static bool TryPlan(
            IEnumerable<EItemType> shopTypes,
            IEnumerable<(EItemType itemType, int restockIndex, int upb, bool bigBox)> offers,
            List<(EItemType itemType, int restockIndex, int boxes, int upb, bool bigBox)> plan,
            Action<string> logInfo, Action<string> logWarn)
        {
            if (_enabled?.Value != true || !AutoWarehouseAccess.Installed)
            {
                _lastError = null;
                string reason = _enabled == null ? "WM config bind not called" :
                    !_enabled.Value ? "WM Auto Warehouse mode disabled" : "Auto Warehouse plugin not found";
                logInfo($"[AW Reserve Check] Using physical warehouse planner: {reason}.");
                return false;
            }

            try
            {
                var aw = AutoWarehouseAccess.Open();
                if (aw == null)
                {
                    logInfo("[AW Reserve Check] Order skipped: Auto Warehouse instance not ready.");
                    return true;
                }

                if (AutoWarehouseAccess.Busy(aw))
                {
                    logInfo($"[AW Reserve Check] Order skipped: Auto Warehouse busy. " +
                        $"ready={AutoWarehouseAccess.Flag(aw, "ready")} " +
                        $"quitting={AutoWarehouseAccess.Flag(aw, "quitting")} " +
                        $"restoreIncomplete={AutoWarehouseAccess.Flag(aw, "warehouseRestoreIncomplete")} " +
                        $"restorePending={AutoWarehouseAccess.Read(aw, "pending") != null} " +
                        $"deletingStock={AutoWarehouseAccess.Flag(aw, "stockDeleteBusy")} " +
                        $"collectingLooseBoxes={AutoWarehouseAccess.Flag(aw, "collectLooseBusy")} " +
                        $"coopClient={AutoWarehouseAccess.Call(aw, "get_CoopClient")}.");
                    return true;
                }
                if (!AutoWarehouseAccess.CanDeliverDirectly(aw))
                    throw new InvalidOperationException("Enable DirectPurchaseDelivery in Auto Warehouse.");

                object? terminal = null;
                var validMachines = new HashSet<object>();
                foreach (var machine in (IEnumerable)AutoWarehouseAccess.Read(aw, "machines")!)
                    if (machine != null && AutoWarehouseAccess.ValidMachine(machine))
                    {
                        validMachines.Add(machine);
                        terminal ??= machine;
                        if (AutoWarehouseAccess.Flag(machine, "autoPurchase"))
                            throw new InvalidOperationException("Turn Auto Order off in Auto Warehouse so WM is the only buyer.");
                    }
                if (terminal == null)
                    throw new InvalidOperationException("Place an active Auto Warehouse terminal.");

                var overrides = ParseOverrides(_overrides!.Value);
                var types = new HashSet<EItemType>(shopTypes);
                foreach (EItemType type in AutoWarehouseAccess.Map(aw, "sharedProducts").Keys)
                    types.Add(type);
                types.UnionWith(overrides.Keys);

                var units = new Dictionary<EItemType, long>();
                foreach (DictionaryEntry entry in AutoWarehouseAccess.Map(aw, "stored"))
                {
                    if (!(entry.Key is InteractablePackagingBox_Item box) || box == null ||
                        entry.Value == null || !validMachines.Contains(entry.Value) ||
                        box.m_ItemCompartment == null)
                        continue;

                    var type = box.GetItemType();
                    types.Add(type);
                    units.TryGetValue(type, out long count);
                    units[type] = count + Math.Max(0, box.m_ItemCompartment.GetItemCount());
                }
                types.Remove(EItemType.None);

                var pending = ReadPendingBoxes();
                var routedInside = new Dictionary<EItemType, long>();
                foreach (var route in (IEnumerable)AutoWarehouseAccess.Read(aw, "orderRoutes")!)
                {
                    if (route == null || AutoWarehouseAccess.Flag(route, "outside"))
                        continue;
                    var machine = AutoWarehouseAccess.Read(route, "machine");
                    if (machine == null || !validMachines.Contains(machine))
                        continue;
                    var type = (EItemType)AutoWarehouseAccess.Read(route, "type")!;
                    routedInside.TryGetValue(type, out long previous);
                    routedInside[type] = previous + Math.Max(0, (int)AutoWarehouseAccess.Read(route, "remaining")!);
                }

                // ReservedStock already includes AW's own inside-order routes, not the
                // remainder of the native delivery queue. Don't reserve either twice.
                int capacity = (int)AutoWarehouseAccess.Call(aw, "SharedCapacity")!;
                int reservedByAw = (int)AutoWarehouseAccess.Call(aw, "ReservedStock", terminal)!;
                long nativePending = 0;
                long pendingNotReserved = 0;
                foreach (var pair in pending)
                {
                    routedInside.TryGetValue(pair.Key, out long covered);
                    nativePending += pair.Value;
                    pendingNotReserved += Math.Max(0, pair.Value - covered);
                }
                long free = Math.Max(0L, (long)capacity - reservedByAw - pendingNotReserved);
                logInfo($"[AW Reserve Check] capacity={capacity} awReserved={reservedByAw} " +
                    $"pendingTotal={nativePending} pendingNotReservedByAW={pendingNotReserved} " +
                    $"freeSlots={free} tracked={types.Count} defaultTarget={_defaultMinimum!.Value}.");

                string diagnosticKey = _diagnosticItem?.Value?.Trim() ?? "";
                EItemType traceType = EItemType.None;
                if (diagnosticKey.Length != 0 && !Enum.TryParse(diagnosticKey, true, out traceType))
                    logInfo($"[AW Reserve Check] Unknown diagnostic item key: {diagnosticKey}.");
                bool traceSeen = false;

                var selected = offers.Where(o => types.Contains(o.itemType) && o.upb > 0)
                    .GroupBy(o => o.itemType)
                    .Select(g => g.OrderByDescending(o => o.bigBox).ThenByDescending(o => o.upb)
                        .ThenBy(o => o.restockIndex).First())
                    .Select(o => (offer: o, units: units.TryGetValue(o.itemType, out var count) ? count : 0))
                    .OrderBy(o => o.units != 0) // out of stock first, then most depleted
                    .ThenBy(o => (double)o.units / o.offer.upb)
                    .ThenBy(o => o.offer.restockIndex);

                var lines = new List<(EItemType, int, int, int, bool)>();
                foreach (var candidate in selected)
                {
                    var o = candidate.offer;
                    if (free <= 0 && o.itemType != traceType)
                        continue;
                    int minimum = overrides.TryGetValue(o.itemType, out int value) ? value : _defaultMinimum!.Value;
                    pending.TryGetValue(o.itemType, out long inFlight);
                    int quantity = BoxesToOrder(minimum, o.upb, candidate.units, inFlight, free);
                    if (o.itemType == traceType)
                    {
                        traceSeen = true;
                        long trigger = Math.Max(0, minimum - 1) * (long)o.upb;
                        string reason = quantity > 0 ? "ready to order" :
                            minimum <= 0 ? "target disabled" :
                            inFlight > 0 ? "delivery pending for this item" :
                            free <= 0 ? "no free slots remaining" : "not below reorder threshold";

                        logInfo($"[AW Reserve Check] {o.itemType}: units={candidate.units} carton={o.upb} " +
                            $"target={minimum} trigger={trigger} pending={inFlight} " +
                            $"freeSlotsAtDecision={free} boxesToOrder={quantity} reason={reason}.");
                    }
                    if (quantity == 0)
                        continue;

                    lines.Add((o.itemType, o.restockIndex, quantity, o.upb, o.bigBox));
                    free -= quantity;
                    long after = candidate.units + (long)quantity * o.upb;
                    logInfo($"[AW Reserve] {o.itemType}: {candidate.units} units in AW, {o.upb} per box, target {minimum}. Ordering {quantity} box(es), projected {after}/{(long)minimum * o.upb} units.");
                }

                if (traceType != EItemType.None && !traceSeen)
                {
                    units.TryGetValue(traceType, out long storedUnits);
                    pending.TryGetValue(traceType, out long pendingBoxes);
                    string reason = types.Contains(traceType) ? "no usable unlocked catalog offer" : "item not tracked";
                    logInfo($"[AW Reserve Check] {traceType}: units={storedUnits} pending={pendingBoxes} " +
                        $"freeSlotsAfterPlan={free} boxesToOrder=0 reason={reason}.");
                }

                plan.AddRange(lines);
                logInfo($"[AW Reserve Check] Plan complete: {lines.Count} products, {lines.Sum(line => line.Item3)} boxes.");
                _lastError = null;
            }
            catch (Exception ex)
            {
                string message = ex.GetBaseException().Message;
                if (_lastError != message)
                    logWarn($"[AW Reserve Check] No order placed: {message}");
                else
                    logInfo($"[AW Reserve Check] Order still blocked: {message}");
                _lastError = message;
            }
            return true;
        }

        internal static int BoxesToOrder(int minimum, int unitsPerBox, long storedUnits,
            long pendingBoxes, long freeSlots)
        {
            if (minimum <= 0 || unitsPerBox <= 0 || pendingBoxes > 0 || freeSlots <= 0)
                return 0;

            long onHand = Math.Max(0, storedUnits);
            // Min 1 is a special case: trigger at zero instead of strictly below zero.
            if (minimum == 1 ? onHand > 0 : onHand >= (long)(minimum - 1) * unitsPerBox)
                return 0;

            long targetUnits = (long)minimum * unitsPerBox;
            long wholeBoxesNeeded = (targetUnits - onHand) / unitsPerBox;
            return (int)Math.Min(wholeBoxesNeeded, freeSlots);
        }

        private static Dictionary<EItemType, int> ParseOverrides(string raw)
        {
            var result = new Dictionary<EItemType, int>();
            foreach (string entry in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                var parts = entry.Split('=');
                if (parts.Length != 2 ||
                    !Enum.TryParse(parts[0].Trim(), true, out EItemType type) || type == EItemType.None ||
                    !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
                    count > 5000)
                    throw new FormatException($"Invalid MinimumBoxesPerItem entry '{entry.Trim()}'. Use catalog-key=0 through 5000.");
                result[type] = count;
            }
            return result;
        }

        private static Dictionary<EItemType, long> ReadPendingBoxes()
        {
            var result = new Dictionary<EItemType, long>();
            var restock = CPlayerData.m_SpawnBoxRestockIndexWaitingList;
            var boxes = CPlayerData.m_SpawnBoxItemCountWaitingList;
            if (restock == null || boxes == null || restock.Count != boxes.Count)
                throw new InvalidOperationException("The native delivery queue is not ready.");
            for (int i = 0; i < restock.Count; i++)
            {
                if (boxes[i] <= 0) continue;
                var row = InventoryBase.GetRestockData(restock[i]);
                if (row == null)
                    throw new InvalidOperationException("A pending delivery has no restock data.");
                result.TryGetValue(row.itemType, out long current);
                result[row.itemType] = current + boxes[i];
            }
            return result;
        }
    }
}
