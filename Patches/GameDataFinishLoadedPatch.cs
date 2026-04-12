#nullable enable

using System.Collections;
using HarmonyLib;
using UnityEngine;
using WarehouseManager.Inventory;

namespace WarehouseManager.Patches
{
    [HarmonyPatch]
    internal static class GameDataFinishLoadedPatch
    {
        private static bool _scheduled;

        static System.Reflection.MethodBase? TargetMethod()
        {
            var mgr = AccessTools.TypeByName("CEventManager");
            if (mgr == null) return null;

            // Try common names across builds
            return AccessTools.Method(mgr, "QueueEvent")
                ?? AccessTools.Method(mgr, "TriggerEvent")
                ?? AccessTools.Method(mgr, "SendEvent");
        }

        // Prefix lets us inspect the event object being queued.
        static void Prefix(object __0)
        {
            if (__0 == null) return;
            if (__0.GetType().Name != "CEventPlayer_GameDataFinishLoaded") return;
            if (_scheduled) return;

            _scheduled = true;

            // lock ordering immediately
            CatalogService.SetRunning(true);

            if (Plugin.Instance == null)
            {
                Plugin.Logger.LogWarning("[CAT] GameDataFinishLoaded observed but Plugin.Instance is null.");
                CatalogService.SetRunning(false);
                return;
            }

            Plugin.Logger.LogInfo("[CAT] GameDataFinishLoaded observed. Scheduling catalog build...");
            Plugin.Instance.StartCoroutine(RunAfterReady());
        }

        private static IEnumerator RunAfterReady()
        {
            for (int i = 0; i < 60; i++)
                yield return null;

            if (!CatalogService.CanBuildNow())
            {
                Plugin.Logger.LogWarning("[CAT] InventoryBase not ready after load; skipping catalog build.");
                CatalogService.SetRunning(false);
                yield break;
            }

            CatalogService.BuildOneShotMerge(Plugin.Instance);
        }
    }
}
