#nullable enable
using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using WarehouseManager.Config;

namespace WarehouseManager.Integration
{
    internal static class ShopManagerHook
    {
        private static bool _init;

        private static Type? _svcType;
        private static MethodInfo? _startMethod;
        private static PropertyInfo? _isRunningProp;

        // Optional logger hooks (same pattern as FateHook)
        private static Action<string>? _logInfo;
        private static Action<string>? _logWarn;
        private static Action<string>? _logDebug;
        private static Func<bool>? _verboseEnabled;

        internal static void Init(
            Action<string>? logInfo = null,
            Action<string>? logWarn = null,
            Action<string>? logDebug = null,
            Func<bool>? verboseEnabled = null)
        {
            if (_init) return;
            _init = true;

            _logInfo = logInfo;
            _logWarn = logWarn;
            _logDebug = logDebug;
            _verboseEnabled = verboseEnabled;

            try
            {
                // ShopRefillService is in ShopManager.Services namespace
                const string typeName = "ShopManager.Services.ShopRefillService";

                _svcType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a =>
                    {
                        try { return a.GetType(typeName, throwOnError: false); }
                        catch { return null; }
                    })
                    .FirstOrDefault(t => t != null);

                if (_svcType == null)
                {
                    DebugLog("[ShopManagerHook] Not found (ShopManager.Services.ShopRefillService missing). Is Shop Manager installed?");
                    return;
                }

                const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                _startMethod = _svcType.GetMethod("Start", BF);
                _isRunningProp = _svcType.GetProperty("IsRunning", BF);

                if (_startMethod == null)
                {
                    WarnLog("[ShopManagerHook] Found type but missing Start().");
                    _svcType = null;
                    return;
                }

                if (_isRunningProp == null)
                {
                    WarnLog("[ShopManagerHook] Found type but missing IsRunning property.");
                    _svcType = null;
                    _startMethod = null;
                    return;
                }

                InfoLog("[ShopManagerHook] Hook ready.");
            }
            catch (Exception ex)
            {
                WarnLog($"[ShopManagerHook] Init failed: {ex}");
                _svcType = null;
                _startMethod = null;
                _isRunningProp = null;
            }
        }

        internal static bool IsReady => _svcType != null && _startMethod != null && _isRunningProp != null;

        internal static bool IsRunning
        {
            get
            {
                if (!IsReady) return false;
                try
                {
                    var v = _isRunningProp!.GetValue(null, null);
                    return v is bool b && b;
                }
                catch { return false; }
            }
        }

        internal static bool TryStart(string phaseTag)
        {
            // WM toggle gate
            if (!WMConfig.ShopManagerEnabled.Value) return false;

            if (!IsReady)
            {
                DebugLog($"[ShopManagerHook] ({phaseTag}) Not initialized (type/method missing).");
                return false;
            }

            try
            {
                if (IsRunning)
                {
                    DebugLog($"[ShopManagerHook] ({phaseTag}) Already running. Not starting a new session.");
                    return true; // treat as “OK / in progress”
                }

                DebugLog($"[ShopManagerHook] ({phaseTag}) Calling ShopRefillService.Start()...");
                _startMethod!.Invoke(null, null);
                InfoLog($"[ShopManagerHook] ({phaseTag}) Started refill session.");
                return true;
            }
            catch (Exception ex)
            {
                WarnLog($"[ShopManagerHook] ({phaseTag}) Start failed: {ex.Message}");
                return false;
            }
        }

        private static void InfoLog(string msg) => _logInfo?.Invoke(msg);
        private static void WarnLog(string msg) => _logWarn?.Invoke(msg);

        private static void DebugLog(string msg)
        {
            if (_verboseEnabled != null && !_verboseEnabled())
                return;
            _logDebug?.Invoke(msg);
        }
    }
}