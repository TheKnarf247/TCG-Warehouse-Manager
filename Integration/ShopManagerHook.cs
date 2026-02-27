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

        private static Type? _refillServiceType;
        private static MethodInfo? _runOnceMethod;

        private static Type? _smConfigType;
        private static FieldInfo? _smEnabledField;

        private static bool _running;

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
                _refillServiceType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a =>
                    {
                        try { return a.GetType("ShopManager.Services.ShopRefillService", false); }
                        catch { return null; }
                    })
                    .FirstOrDefault(t => t != null);

                if (_refillServiceType == null)
                {
                    DebugLog("[ShopManager] Not found (ShopManager.Services.ShopRefillService missing). Is Shop Manager installed?");
                    return;
                }

                const BindingFlags BF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _runOnceMethod = _refillServiceType.GetMethod("RunOnce", BF);

                if (_runOnceMethod == null)
                {
                    WarnLog("[ShopManager] Found type but not method RunOnce().");
                    _refillServiceType = null;
                    return;
                }

                _smConfigType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a =>
                    {
                        try { return a.GetType("ShopManager.Config.SMConfig", false); }
                        catch { return null; }
                    })
                    .FirstOrDefault(t => t != null);

                if (_smConfigType != null)
                {
                    _smEnabledField = _smConfigType.GetField("Enabled",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                InfoLog("[ShopManager] Hook ready.");
            }
            catch (Exception ex)
            {
                WarnLog($"[ShopManager] Init failed: {ex}");
                _refillServiceType = null;
                _runOnceMethod = null;
                _smConfigType = null;
                _smEnabledField = null;
            }
        }

        internal static bool TryRun(string phaseTag)
        {
            if (!WMConfig.ShopManagerEnabled.Value)
                return false;

            if (_refillServiceType == null || _runOnceMethod == null)
            {
                DebugLog($"[ShopManager] ({phaseTag}) Not initialized (type/method missing).");
                return false;
            }

            if (_running)
            {
                WarnLog($"[ShopManager] ({phaseTag}) Already running. Skipping.");
                return false;
            }

            if (!IsEnabledInShopManagerConfig(out var why))
            {
                WarnLog($"[ShopManager] ({phaseTag}) Installed but disabled in Shop Manager config ({why}). Skipping.");
                return false;
            }

            try
            {
                _running = true;
                DebugLog($"[ShopManager] ({phaseTag}) Running ShopRefillService.RunOnce()...");
                _runOnceMethod.Invoke(null, null);
                InfoLog($"[ShopManager] ({phaseTag}) Ran ShopRefillService.RunOnce().");
                return true;
            }
            catch (Exception ex)
            {
                WarnLog($"[ShopManager] ({phaseTag}) Hook failed: {ex.Message}");
                return false;
            }
            finally
            {
                _running = false;
            }
        }

        private static bool IsEnabledInShopManagerConfig(out string reason)
        {
            reason = "unknown";

            if (_smConfigType == null || _smEnabledField == null)
            {
                reason = "SMConfig not found";
                return true; // treat as enabled if we can't read it
            }

            try
            {
                object? ceObj = _smEnabledField.GetValue(null);
                if (ceObj == null)
                {
                    reason = "Enabled field null";
                    return true;
                }

                if (ceObj is ConfigEntry<bool> ceBool)
                {
                    reason = ceBool.Value ? "enabled" : "disabled";
                    return ceBool.Value;
                }

                var valueProp = ceObj.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                if (valueProp != null)
                {
                    var v = valueProp.GetValue(ceObj, null);
                    if (v is bool b)
                    {
                        reason = b ? "enabled" : "disabled";
                        return b;
                    }

                    if (v != null && bool.TryParse(v.ToString(), out bool parsed))
                    {
                        reason = parsed ? "enabled" : "disabled";
                        return parsed;
                    }
                }

                reason = "could not read Enabled.Value";
                return true;
            }
            catch
            {
                reason = "exception reading config";
                return true;
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