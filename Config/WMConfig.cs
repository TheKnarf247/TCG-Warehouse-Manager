#nullable enable
using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace WarehouseManager.Config
{
    internal static class WMConfig
    {
        internal static ConfigEntry<bool> Enabled = null!;

        internal static ConfigEntry<KeyboardShortcut> ManualHotkey = null!;
        internal static ConfigEntry<KeyboardShortcut> RebuildDatabaseHotkey = null!;
        internal static ConfigEntry<KeyboardShortcut> DebugCombinedScanHotkey = null!;

        internal static ConfigEntry<bool> EveryXMinutesEnabled = null!;
        internal static ConfigEntry<int> EveryXMinutes = null!; // 1..10

        internal static ConfigEntry<bool> OncePerDayEnabled = null!;
        internal static ConfigEntry<string> OncePerDayHHmm = null!; // "08:00" .. "21:00"

        internal static ConfigEntry<bool> FateEnabled = null!;
        internal static ConfigEntry<string> DestinyComingSoon = null!;

        // Debug suggestions
        internal static ConfigEntry<bool> DebugEnabled = null!;
        internal static ConfigEntry<bool> VerboseLogging = null!;
        internal static ConfigEntry<bool> DryRunPlanOnly = null!;
        internal static ConfigEntry<int> DumpMaxEntries = null!;

        internal static void Bind(ConfigFile cfg)
        {
            // 01 - General
            Enabled = cfg.Bind("01 - General", "Enabled", true,
                "Enable/disable Warehouse Manager entirely.");

            ManualHotkey = cfg.Bind("01 - General", "Manual hotkey",
                new KeyboardShortcut(KeyCode.F1, KeyCode.LeftShift),
                "Hotkey to execute ordering manually.");

            RebuildDatabaseHotkey = cfg.Bind("01 - General", "Rebuild database hotkey",
                new KeyboardShortcut(KeyCode.F9, KeyCode.LeftShift),
                "Hotkey to rebuild the item catalog/database manually.");

            // 02 - Automation
            EveryXMinutesEnabled = cfg.Bind("02 - Automation", "EveryXMinutesEnabled", false,
                "Enable/disable real-time interval automation.");

            EveryXMinutes = cfg.Bind("02 - Automation", "EveryXMinutes", 5,
                new ConfigDescription("Run every X real-world minutes (1-10).",
                    new AcceptableValueRange<int>(1, 10)));

            OncePerDayEnabled = cfg.Bind("02 - Automation", "Once a day", false,
                "Enable/disable once-per-day (in-game time) automation.");

            OncePerDayHHmm = cfg.Bind("02 - Automation", "SpecificTimeHHmm", "08:00",
                "In-game time to run once-per-day, format HH:MM (08:00 - 21:00).");

            // 03 - Integration
            FateEnabled = cfg.Bind("03 - Integration", "F(ATE) Enabled", false,
                "If enabled, runs the F(ATE) integration before and after an order execution.");

            DestinyComingSoon = cfg.Bind("03 - Integration", "D(ESTINY)", "Coming Soon",
                "Coming Soon: Auto-restock shop shelves from inventory.");

            // 90 - Debug
            DebugEnabled = cfg.Bind("90 - Debug", "Enabled", false,
                "Enable debug features/logging.");

            VerboseLogging = cfg.Bind("90 - Debug", "VerboseLogging", false,
                "More detailed logging to help troubleshoot.");

            DryRunPlanOnly = cfg.Bind("90 - Debug", "DryRunPlanOnly", false,
                "Build the plan but do NOT add to cart / checkout.");

            DumpMaxEntries = cfg.Bind("90 - Debug", "DumpMaxEntries", 40,
                "Maximum entries to dump in debug output.");
            
            DebugCombinedScanHotkey = cfg.Bind("90 - Debug", "DebugCombinedScanHotkey",
                new KeyboardShortcut(KeyCode.F9, KeyCode.LeftShift),
                "Hotkey to run the combined inventory scan (debug).");

        }

        internal static bool TryGetOncePerDayTime(out TimeSpan time)
        {
            time = default;
            var s = (OncePerDayHHmm.Value ?? "").Trim();

            if (!TimeSpan.TryParseExact(s, "hh\\:mm", CultureInfo.InvariantCulture, out var t))
                return false;

            // constrain: 08:00 - 21:00
            if (t < new TimeSpan(8, 0, 0) || t > new TimeSpan(21, 0, 0))
                return false;

            time = t;
            return true;
        }
    }
}
