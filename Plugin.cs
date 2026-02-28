using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using System;
using WarehouseManager.Ordering;
using WarehouseManager.Config;
using WarehouseManager.Integration;
using WarehouseManager.Logging;
using WarehouseManager.Automation;
using System.Collections;

namespace WarehouseManager
{
    [BepInPlugin(
        MyPluginInfo.PLUGIN_GUID,
        MyPluginInfo.PLUGIN_NAME,
        MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger { get; private set; } = null!;
        internal static Plugin Instance { get; private set; } = null!;
        private readonly Harmony _harmony = new(MyPluginInfo.PLUGIN_GUID);

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;

            // Config FIRST
            WMConfig.Bind(Config);

            // File logger + log levels
            WMFileLogger.Init();
            ApplyLogLevels();

            // Live update when config changes
            WMConfig.DebugEnabled.SettingChanged += (_, __) => ApplyLogLevels();
            WMConfig.VerboseLogging.SettingChanged += (_, __) => ApplyLogLevels();

            _harmony.PatchAll();

            OncePerDayScheduler.Init(
                LogInfo,
                LogWarn,
                LogVerbose,
                () => WMConfig.VerboseLogging.Value
            );

            EveryXMinutesScheduler.Init(
                LogInfo,
                LogWarn,
                LogVerbose,
                () => WMConfig.VerboseLogging.Value
            );

            // Integrations
            ShopManagerHook.Init(
                LogInfo,
                LogWarn,
                LogVerbose,
                () => WMConfig.VerboseLogging.Value
            );

            // NOTE: FateHook removed. Stocker replaces it.
            LogInfo($"Plugin {MyPluginInfo.PLUGIN_NAME} v:{MyPluginInfo.PLUGIN_VERSION} by Knarf Hasn't Exploded Yet!");
        }

        private static void ApplyLogLevels()
        {
            bool dbg = WMConfig.DebugEnabled.Value;
            bool verb = WMConfig.VerboseLogging.Value;
            WMFileLogger.SetLevels(dbg, verb);

            // Also reflect in console (so you can confirm toggles are active)
            Logger?.LogInfo($"[LOG] Levels applied: Debug={dbg} Verbose={verb}");
        }

        // -----------------------------------------------------------------
        // Unified logging helpers (BepInEx + file logger always in sync)
        // -----------------------------------------------------------------
        internal static void LogInfo(string msg)
        {
            Logger.LogInfo(msg);
            WMFileLogger.Info(msg);
        }

        internal static void LogWarn(string msg)
        {
            Logger.LogWarning(msg);
            WMFileLogger.Warn(msg);
        }

        internal static void LogError(string msg)
        {
            Logger.LogError(msg);
            WMFileLogger.Error(msg);
        }

        internal static void LogDebug(string msg)
        {
            if (!WMConfig.DebugEnabled.Value) return;
            Logger.LogInfo(msg);
            WMFileLogger.Debug(msg);
        }

        internal static void LogVerbose(string msg)
        {
            if (!WMConfig.VerboseLogging.Value) return;
            Logger.LogInfo(msg);
            WMFileLogger.Verbose(msg);
        }

        private void Update()
        {
            Hotkey.Update();
            EveryXMinutesScheduler.UpdateTick();
        }

        private void OnDestroy()
        {
            OncePerDayScheduler.Shutdown();
            WMFileLogger.Shutdown();
        }

        // Prevent stacking multiple WM runs while we’re waiting for Shop Manager
        private static bool _wmRunInFlight;

        // -----------------------------------------------------------------
        // One wrapper to enforce: Shop Manager (wait) -> WM -> Stocker (after)
        // -----------------------------------------------------------------
        internal static void RunWithIntegrations(string reasonTag, Action wmCoreRun)
        {
            if (_wmRunInFlight)
            {
                LogWarn($"[WM] RunWithIntegrations ignored; previous run still in-flight ({reasonTag}).");
                return;
            }

            _wmRunInFlight = true;
            try
            {
                Instance.StartCoroutine(RunWithIntegrations_Coroutine(reasonTag, wmCoreRun));
            }
            catch (Exception ex)
            {
                _wmRunInFlight = false;
                LogWarn($"[WM] Failed to start coroutine ({reasonTag}): {ex}");
            }
        }

        private static IEnumerator RunWithIntegrations_Coroutine(string reasonTag, Action wmCoreRun)
        {
            // 1) Shop Manager first (wait until done)
            if (WMConfig.ShopManagerEnabled.Value)
            {
                ShopManagerHook.TryStart($"Before WM ({reasonTag})");

                float startedAt = Time.realtimeSinceStartup;
                const float hardTimeoutSeconds = 120f;

                while (ShopManagerHook.IsRunning)
                {
                    if (Time.realtimeSinceStartup - startedAt > hardTimeoutSeconds)
                    {
                        LogWarn($"[WM] Shop Manager wait timed out after {hardTimeoutSeconds}s ({reasonTag}). Continuing WM anyway.");
                        break;
                    }

                    yield return null;
                }
            }

            // 2) WM core execution
            try
            {
                wmCoreRun.Invoke();
            }
            catch (Exception ex)
            {
                LogWarn($"[WM] WM core run failed ({reasonTag}): {ex}");
            }

            // 3) Wait for WM ordering to actually produce boxes, then Stocker

            if (WMConfig.StockerEnabled.Value)
            {
                int beforeCount = 0;

                try
                {
                    var pre = RestockManager.GetItemPackagingBoxListWithItem(true);
                    beforeCount = pre?.Count ?? 0;
                }
                catch { }

                // ---- WAIT FOR SPAWN (NO try/catch around yield) ----
                float startWait = Time.realtimeSinceStartup;
                const float spawnWaitSeconds = 10f;
                bool sawIncrease = false;

                while (Time.realtimeSinceStartup - startWait < spawnWaitSeconds)
                {
                    int nowCount = 0;

                    try
                    {
                        var cur = RestockManager.GetItemPackagingBoxListWithItem(true);
                        nowCount = cur?.Count ?? 0;
                    }
                    catch { }

                    if (nowCount > beforeCount)
                    {
                        sawIncrease = true;
                        break;
                    }

                    yield return null; // ✅ safe now
                }

                // ---- SETTLE DELAY ----
                float settleStart = Time.realtimeSinceStartup;
                const float settleSeconds = 2f;

                while (Time.realtimeSinceStartup - settleStart < settleSeconds)
                    yield return null;

                // ---- NOW RUN STOCKER (wrap this only) ----
                try
                {
                    Stocker.TryRun($"After WM (post-order) ({reasonTag})");

                    if (WMConfig.VerboseLogging.Value)
                        LogVerbose($"[WM] Stocker post-order wait: sawIncrease={sawIncrease}, beforeBoxes={beforeCount}");
                }
                catch (Exception ex)
                {
                    LogWarn($"[WM] Stocker after-run failed ({reasonTag}): {ex}");
                }
            }

            _wmRunInFlight = false;
        }
    }
}