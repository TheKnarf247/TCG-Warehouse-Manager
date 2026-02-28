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

        // -----------------------------------------------------------------
        // One wrapper to enforce run order around ANY WM execution
        // -----------------------------------------------------------------
        internal static void RunWithIntegrations(string reasonTag, Action wmCoreRun)
        {
            try
            {
                // Shop Manager BEFORE WM only
                ShopManagerHook.TryRun($"Before WM ({reasonTag})");

                // WM core
                wmCoreRun.Invoke();

                // Stocker AFTER WM only (replaces F(ATE))
                Stocker.TryRun($"After WM ({reasonTag})");
            }
            catch (Exception ex)
            {
                LogWarn($"[WM] RunWithIntegrations failed ({reasonTag}): {ex}");
            }
        }
    }
}