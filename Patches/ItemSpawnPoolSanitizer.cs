#nullable enable
using System.Collections.Generic;
using HarmonyLib;

namespace WarehouseManager.Patches
{
    [HarmonyPatch(typeof(ItemSpawnManager), "GetOrCreateItem")]
    internal static class ItemSpawnPoolSanitizer
    {
        [HarmonyPrefix]
        private static void Prefix(Stack<Item> ___m_FreeItems)
        {
            int discarded = 0;

            // Unity's null check also catches destroyed native objects whose managed
            // references are still on the stack.
            while (___m_FreeItems.Count > 0 &&
                   (UnityEngine.Object)___m_FreeItems.Peek() == null)
            {
                ___m_FreeItems.Pop();
                discarded++;
            }

            if (discarded > 0)
                global::WarehouseManager.Plugin.LogWarn(
                    $"[ItemPool] Discarded {discarded} invalid free item(s) before checkout.");
        }
    }
}
