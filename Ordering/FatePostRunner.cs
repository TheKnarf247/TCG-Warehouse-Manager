#nullable enable
using System.Collections;
using UnityEngine;
using WarehouseManager.Integration;

namespace WarehouseManager.Ordering
{
    internal static class FatePostRunner
    {
        private sealed class Runner : MonoBehaviour { }

        private static Runner? _runner;

        private static Runner GetRunner()
        {
            if (_runner != null) return _runner;

            var go = new GameObject("WarehouseManager_FatePostRunner");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
            return _runner;
        }

        internal static void RunDelayed(float seconds)
        {
            GetRunner().StartCoroutine(Run(seconds));
        }

        private static IEnumerator Run(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            FateHook.TryRun("POST");
        }
    }
}
