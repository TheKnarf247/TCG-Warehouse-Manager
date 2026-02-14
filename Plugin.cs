using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using BepInEx.Configuration;
using WarehouseManager.Ordering;
using WarehouseManager.Inventory;
using WarehouseManager.Patches;
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
            _harmony.PatchAll();
            WMFileLogger.Init();
            WMConfig.Bind(Config);

            OncePerDayScheduler.Init(
                s => { Logger.LogInfo(s); WMFileLogger.Info(s); },
                s => { Logger.LogWarning(s); WMFileLogger.Warn(s); },
                s => { Logger.LogInfo(s); WMFileLogger.Info(s); },
                () => WMConfig.VerboseLogging.Value
            );
            
            EveryXMinutesScheduler.Init(
                s => { Logger.LogInfo(s); WMFileLogger.Info(s); },
                s => { Logger.LogWarning(s); WMFileLogger.Warn(s); },
                s => { if (WMConfig.VerboseLogging.Value) { Logger.LogInfo(s); WMFileLogger.Info(s); } },
                () => WMConfig.VerboseLogging.Value
            );

            FateHook.Init(
                s => { Logger.LogInfo(s); WMFileLogger.Info(s); },
                s => { Logger.LogWarning(s); WMFileLogger.Warn(s); },
                s =>
                {
                    if (WMConfig.VerboseLogging.Value)
                    {
                        Logger.LogInfo(s);
                        WMFileLogger.Info(s);
                    }
                },
                () => WMConfig.VerboseLogging.Value
            );

            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_NAME} v:{MyPluginInfo.PLUGIN_VERSION} by Knarf Hasn't Exploded Yet!");
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
    }
}