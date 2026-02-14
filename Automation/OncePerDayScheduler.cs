#nullable enable
using System;
using HarmonyLib;
using UnityEngine;
using WarehouseManager.Config;
using WarehouseManager.Ordering;

namespace WarehouseManager.Automation
{
    internal static class OncePerDayScheduler
    {
        private static bool _init;

        // State
        private static bool _firedToday;
        private static bool _armedForToday;

        // Track clock progress
        private static int _lastSeenMinuteOfDay = -1;

        private static Action<string>? _info;
        private static Action<string>? _warn;
        private static Action<string>? _debug;
        private static Func<bool>? _verbose;

        // IMPORTANT: store delegate instances so RemoveListener works reliably.
        private static CEventManager.EventDelegate<CEventPlayer_OnDayStarted>? _onDayStartedDel;
        private static CEventManager.EventDelegate<CEventPlayer_OnDayEnded>? _onDayEndedDel;
        private static CEventManager.EventDelegate<CEventPlayer_GameDataFinishLoaded>? _onGameDataFinishLoadedDel;

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

            try
            {
                _onDayStartedDel = new CEventManager.EventDelegate<CEventPlayer_OnDayStarted>(OnDayStarted);
                _onDayEndedDel = new CEventManager.EventDelegate<CEventPlayer_OnDayEnded>(OnDayEnded);
                _onGameDataFinishLoadedDel = new CEventManager.EventDelegate<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);

                CEventManager.AddListener<CEventPlayer_OnDayStarted>(_onDayStartedDel);
                CEventManager.AddListener<CEventPlayer_OnDayEnded>(_onDayEndedDel);
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(_onGameDataFinishLoadedDel);

                // Default state: allow firing today unless load-time safety disarms us
                _firedToday = false;
                _armedForToday = true;
                _lastSeenMinuteOfDay = -1;

                _info?.Invoke("[OncePerDay] Scheduler initialized (self-healing clock logic + optional DayStarted/DayEnded).");
            }
            catch (Exception ex)
            {
                _warn?.Invoke($"[OncePerDay] Init failed: {ex}");
            }
        }

        internal static void Shutdown()
        {
            if (!_init) return;

            try
            {
                if (_onDayStartedDel != null) CEventManager.RemoveListener<CEventPlayer_OnDayStarted>(_onDayStartedDel);
                if (_onDayEndedDel != null) CEventManager.RemoveListener<CEventPlayer_OnDayEnded>(_onDayEndedDel);
                if (_onGameDataFinishLoadedDel != null) CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(_onGameDataFinishLoadedDel);
            }
            catch
            {
                // ignore
            }

            _onDayStartedDel = null;
            _onDayEndedDel = null;
            _onGameDataFinishLoadedDel = null;

            _init = false;
        }

        // ----------------------------
        // Optional Event Handlers (nice to have, NOT required for correctness)
        // ----------------------------

        private static void OnDayStarted(CEventPlayer_OnDayStarted evt)
        {
            // Canonical reset
            _firedToday = false;
            _armedForToday = true;
            _lastSeenMinuteOfDay = -1;

            _info?.Invoke("[OncePerDay] OnDayStarted received → armed.");
        }

        private static void OnDayEnded(CEventPlayer_OnDayEnded evt)
        {
            // Canonical disarm
            _armedForToday = false;
            _info?.Invoke("[OncePerDay] OnDayEnded received → disarmed.");
        }

        private static void OnGameDataFinishLoaded(CEventPlayer_GameDataFinishLoaded evt)
        {
            if (!WMConfig.Enabled.Value || !WMConfig.OncePerDayEnabled.Value)
            {
                DebugLog("[OncePerDay] GameDataFinishLoaded → disabled by config.");
                return;
            }

            if (!WMConfig.TryGetOncePerDayTime(out var target))
            {
                _warn?.Invoke("[OncePerDay] GameDataFinishLoaded → invalid SpecificTimeHHmm; will not fire.");
                _armedForToday = false;
                return;
            }

            // Safety: do not “surprise fire” immediately on load if we’re already past target.
            var lm = FindLightManager();
            if (lm == null)
            {
                _armedForToday = true;
                DebugLog("[OncePerDay] GameDataFinishLoaded → LightManager not found; armed by default.");
                return;
            }

            int curMin = (lm.m_TimeHour * 60) + lm.m_TimeMin;
            int targetMin = (int)target.TotalMinutes;

            // If day is already ended, disarm
            if (lm.m_TimeHour == 21 && lm.m_TimeMin == 0)
            {
                _armedForToday = false;
                DebugLog("[OncePerDay] GameDataFinishLoaded → already at 21:00; disarmed.");
                return;
            }

            _armedForToday = curMin < targetMin;
            DebugLog($"[OncePerDay] GameDataFinishLoaded → cur={lm.m_TimeHour:D2}:{lm.m_TimeMin:D2} ({curMin}), target={targetMin}, armed={_armedForToday}.");
        }

        private static LightManager? FindLightManager()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<LightManager>();
                if (all == null || all.Length == 0) return null;

                foreach (var lm in all)
                {
                    if (lm != null && lm.gameObject != null && lm.gameObject.scene.IsValid())
                        return lm;
                }

                return all[0];
            }
            catch
            {
                return null;
            }
        }

        // ----------------------------
        // Clock Tick (called by Harmony patch below)
        // ----------------------------

        internal static void OnClockTick(int hour, int min)
        {
            if (!WMConfig.Enabled.Value) return;
            if (!WMConfig.OncePerDayEnabled.Value) return;

            if (!WMConfig.TryGetOncePerDayTime(out var target))
                return;

            int cur = (hour * 60) + min;
            int targetMin = (int)target.TotalMinutes;

            // If time goes backwards (21:00 -> 08:00) treat as new day
            if (_lastSeenMinuteOfDay != -1 && cur < _lastSeenMinuteOfDay)
            {
                _firedToday = false;
                _armedForToday = true;
                DebugLog($"[OncePerDay] Time wrapped backwards ({_lastSeenMinuteOfDay} -> {cur}) → new day armed.");
            }

            // Avoid repeats in same minute (Update runs every frame)
            if (_lastSeenMinuteOfDay == cur)
                return;

            // --- Change #2: only reset at 08:00 once per minute (now safe because minute gate is above) ---
            if (hour == 8 && min == 0)
            {
                _firedToday = false;
                _armedForToday = true;
                DebugLog("[OncePerDay] Detected 08:00 → armed/reset.");
            }

            // --- Change #1: don't block a valid 21:00 trigger by disarming first ---
            // Disarm at hard-stop 21:00 (game freezes time there until next day)
            // BUT: don't block a valid 21:00 trigger.
            if (hour == 21 && min == 0)
            {
                if (targetMin < 21 * 60 || _firedToday)
                {
                    _armedForToday = false;
                    DebugLog("[OncePerDay] Detected 21:00 → disarmed (day end hard-stop).");
                }
            }

            _lastSeenMinuteOfDay = cur;

            DebugLog($"[OncePerDay] Tick {hour:D2}:{min:D2} cur={cur} target={targetMin} fired={_firedToday} armed={_armedForToday}");

            if (!_armedForToday) return;
            if (_firedToday) return;

            if (cur >= targetMin)
            {
                _firedToday = true;
                _armedForToday = false;

                _info?.Invoke($"[OncePerDay] Trigger fired at {hour:D2}:{min:D2}.");
                OrderExecutor.RunOrder_FromCatalog();
            }
        }

        private static void DebugLog(string msg)
        {
            if (_verbose != null && !_verbose())
                return;
            _debug?.Invoke(msg);
        }
    }

    // ----------------------------
    // Harmony Patch: LightManager.Update (reliable per-frame tick; minute gated inside scheduler)
    // ----------------------------
    [HarmonyPatch(typeof(LightManager))]
    internal static class OncePerDayClockPatch
    {
        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        private static void LightManagerUpdatePostfix(LightManager __instance)
        {
            OncePerDayScheduler.OnClockTick(__instance.m_TimeHour, __instance.m_TimeMin);
        }

        // Keep this too — harmless, sometimes gives earlier tick in some builds
        [HarmonyPatch(nameof(LightManager.EvaluateTimeClock))]
        [HarmonyPostfix]
        private static void EvaluateTimeClockPostfix(LightManager __instance)
        {
            OncePerDayScheduler.OnClockTick(__instance.m_TimeHour, __instance.m_TimeMin);
        }
    }
}
