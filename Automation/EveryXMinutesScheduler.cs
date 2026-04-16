#nullable enable
using System;
using UnityEngine;
using WarehouseManager.Config;
using WarehouseManager.Ordering;

namespace WarehouseManager.Automation
{
    internal static class EveryXMinutesScheduler
    {
        private static bool _init;

        private static Action<string>? _info;
        private static Action<string>? _warn;
        private static Action<string>? _debug;
        private static Func<bool>? _verbose;

        private static float _nextRunAtRealtime = -1f;
        private static int _lastConfiguredMinutes = -1;
        private static bool _wasEnabledLastTick;

        internal static void Init(
            Action<string>? logInfo = null,
            Action<string>? logWarn = null,
            Action<string>? logDebug = null,
            Func<bool>? verboseEnabled = null)
        {
            if (_init) return;
            _init = true;

            _info = logInfo;
            _warn = logWarn;
            _debug = logDebug;
            _verbose = verboseEnabled;

            _nextRunAtRealtime = -1f;
            _lastConfiguredMinutes = -1;
            _wasEnabledLastTick = false;

            _info?.Invoke("[EveryXMinutes] Scheduler initialized.");
        }

        internal static void Shutdown()
        {
            _init = false;
            _nextRunAtRealtime = -1f;
            _lastConfiguredMinutes = -1;
            _wasEnabledLastTick = false;
        }

        /// <summary>
        /// Call this once per frame from Plugin.Update().
        /// </summary>
        internal static void UpdateTick()
        {
            if (!_init) return;

            // Master enable gates
            if (!WMConfig.Enabled.Value || !WMConfig.EveryXMinutesEnabled.Value)
            {
                if (_wasEnabledLastTick)
                {
                    _wasEnabledLastTick = false;
                    _nextRunAtRealtime = -1f;
                    DebugLog("[EveryXMinutes] Disabled -> timer cleared.");
                }
                return;
            }

            _wasEnabledLastTick = true;

            // Suspend while paused / in menus, and keep pushing the timer forward
            // so runs do not stack up in the background.
            if (IsGamePausedOrInMenu())
            {
                if (_nextRunAtRealtime > 0f)
                    _nextRunAtRealtime = Time.realtimeSinceStartup + 1f;

                DebugLog("[EveryXMinutes] Paused/UI mode -> scheduler suspended.");
                return;
            }

            int minutes = Mathf.Clamp(WMConfig.EveryXMinutes.Value, 1, 10);

            // If config changed, re-arm immediately (but don’t run instantly—schedule from now)
            if (minutes != _lastConfiguredMinutes)
            {
                _lastConfiguredMinutes = minutes;
                _nextRunAtRealtime = Time.realtimeSinceStartup + (minutes * 60f);
                _info?.Invoke($"[EveryXMinutes] Interval set to {minutes} minute(s). Next run scheduled.");
            }

            // First-time arm
            if (_nextRunAtRealtime < 0f)
            {
                _nextRunAtRealtime = Time.realtimeSinceStartup + (minutes * 60f);
                DebugLog("[EveryXMinutes] First arm -> next run scheduled.");
                return;
            }

            if (Time.realtimeSinceStartup < _nextRunAtRealtime)
                return;

            // Schedule next run BEFORE executing (prevents double-fire if execution takes time)
            _nextRunAtRealtime = Time.realtimeSinceStartup + (minutes * 60f);

            _info?.Invoke($"[EveryXMinutes] Trigger fired (every {minutes} min). Executing order...");
            try
            {
                Plugin.RunWithIntegrations("EveryXMinutes", () => OrderExecutor.RunOrder_FromCatalog());
            }
            catch (Exception ex)
            {
                _warn?.Invoke($"[EveryXMinutes] Order execution failed: {ex}");
            }
        }

        private static bool IsGamePausedOrInMenu()
        {
            return Time.timeScale <= 0f;
        }

        private static void DebugLog(string msg)
        {
            if (_verbose != null && !_verbose())
                return;
            _debug?.Invoke(msg);
        }
    }
}