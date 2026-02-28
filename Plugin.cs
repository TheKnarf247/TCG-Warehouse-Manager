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
using System.Collections.Generic;

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

        /// <summary>
        /// Returns true if at least one packaging box exists that Stocker would consider "ready" to move.
        /// Also returns total/ready counts for debug logging.
        /// </summary>
        private static bool IsAnyStockerReadyBox(out int totalBoxes, out int readyBoxes)
        {
            totalBoxes = 0;
            readyBoxes = 0;

            List<InteractablePackagingBox_Item>? boxes = null;
            try
            {
                boxes = RestockManager.GetItemPackagingBoxListWithItem(true);
            }
            catch
            {
                return false;
            }

            if (boxes == null) return false;

            totalBoxes = boxes.Count;

            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                if (b == null) continue;

                if (b.m_IsStored) continue;
                if (b.IsBoxOpened()) continue;
                if (b.m_Collider != null && !b.m_Collider.enabled) continue;
                if (b.GetItemType() == EItemType.None) continue;

                int count = 0;
                try
                {
                    count = b.m_ItemCompartment != null ? b.m_ItemCompartment.GetItemCount() : 0;
                }
                catch
                {
                    count = 0;
                }

                if (count <= 0) continue;

                readyBoxes++;
                if (readyBoxes >= 1)
                    return true;
            }

            return false;
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

            // 3) Simple fixed delay before Stocker
            if (WMConfig.StockerEnabled.Value)
            {
                const float stockerDelaySeconds = 6f;

                if (WMConfig.VerboseLogging.Value)
                    LogVerbose($"[WM] Waiting {stockerDelaySeconds}s before running Stocker... ({reasonTag})");

                float delayStart = Time.realtimeSinceStartup;

                while (Time.realtimeSinceStartup - delayStart < stockerDelaySeconds)
                    yield return null;

                try
                {
                    Stocker.TryRun($"After WM (6s delay) ({reasonTag})");
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