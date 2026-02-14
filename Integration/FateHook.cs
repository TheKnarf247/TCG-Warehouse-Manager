#nullable enable
using BepInEx.Configuration;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using WarehouseManager.Config;

namespace WarehouseManager.Integration
{
    internal static class FateHook
    {
        private static bool _init;
        private static Type? _fateType;
        private static MethodInfo? _moveMethod;
        private static FieldInfo? _isRunningField;
        private static FieldInfo? _modEnabledField;

        // Optional logger hooks (so this file doesn't care what your logger type is)
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
                // Fate class is in global namespace, named "Fate"
                _fateType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a =>
                    {
                        try { return a.GetTypes().FirstOrDefault(t => t.Name == "Fate"); }
                        catch { return null; }
                    })
                    .FirstOrDefault(t => t != null);

                if (_fateType == null)
                {
                    DebugLog("[FATE] Not found (type 'Fate' missing). Is F(ate) installed?");
                    return;
                }

                const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _moveMethod = _fateType.GetMethod("MoveFloorBoxesToWarehouse", BF);
                _isRunningField = _fateType.GetField("_isRunning", BF);
                _modEnabledField = _fateType.GetField("_modEnabled", BF);

                if (_moveMethod == null)
                {
                    WarnLog("[FATE] Found type but not method MoveFloorBoxesToWarehouse().");
                    _fateType = null;
                    return;
                }

                InfoLog("[FATE] Hook ready.");
            }
            catch (Exception ex)
            {
                WarnLog($"[FATE] Init failed: {ex}");
                _fateType = null;
                _moveMethod = null;
                _isRunningField = null;
                _modEnabledField = null;
            }
        }

        internal static bool TryRun(string phaseTag)
        {
            // WM toggle gate
            if (!WMConfig.FateEnabled.Value) return false;

            if (_fateType == null || _moveMethod == null)
            {
                DebugLog($"[FATE] ({phaseTag}) Not initialized (type/method missing).");
                return false;
            }

            try
            {
                // Find Fate instance the reliable way (works even if inactive / not scene-findable)
                var all = Resources.FindObjectsOfTypeAll(_fateType);
                if (all == null || all.Length == 0)
                {
                    WarnLog($"[FATE] ({phaseTag}) Found type but no instance was active.");
                    return false;
                }

                object? fateObj = null;

                // Prefer a scene-valid instance if possible, but DO NOT require it.
                foreach (var o in all)
                {
                    if (o == null) continue;

                    if (o is Component c && c.gameObject != null && c.gameObject.scene.IsValid())
                    {
                        fateObj = o;
                        break;
                    }

                    if (fateObj == null)
                        fateObj = o;
                }

                if (fateObj == null)
                {
                    WarnLog($"[FATE] ({phaseTag}) Found type but instance was null.");
                    return false;
                }

                const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Respect Fate's own Enabled config: private ConfigEntry<bool> _modEnabled;
                if (_modEnabledField != null)
                {
                    try
                    {
                        var ceObj = _modEnabledField.GetValue(fateObj);
                        if (ceObj != null)
                        {
                            // Strong cast first
                            if (ceObj is ConfigEntry<bool> ceBool)
                            {
                                if (!ceBool.Value)
                                {
                                    WarnLog($"[FATE] ({phaseTag}) Installed, but disabled in its own config. Skipping.");
                                    return false;
                                }
                            }
                            else
                            {
                                // Reflection fallback: read Value property
                                var valueProp = ceObj.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                                if (valueProp != null)
                                {
                                    var v = valueProp.GetValue(ceObj, null);
                                    if (v is bool b && !b)
                                    {
                                        WarnLog($"[FATE] ({phaseTag}) Installed, but disabled in its own config. Skipping.");
                                        return false;
                                    }
                                    if (v != null && bool.TryParse(v.ToString(), out bool parsed) && !parsed)
                                    {
                                        WarnLog($"[FATE] ({phaseTag}) Installed, but disabled in its own config. Skipping.");
                                        return false;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // ignore - treat as enabled if we can't read it
                    }
                }

                // Avoid collisions: private bool _isRunning;
                bool toggledRunning = false;
                if (_isRunningField != null && _isRunningField.FieldType == typeof(bool))
                {
                    try
                    {
                        bool isRunning = (bool)_isRunningField.GetValue(fateObj);
                        if (isRunning)
                        {
                            WarnLog($"[FATE] ({phaseTag}) Already running. Skipping.");
                            return false;
                        }

                        _isRunningField.SetValue(fateObj, true);
                        toggledRunning = true;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                try
                {
                    DebugLog($"[FATE] ({phaseTag}) Running MoveFloorBoxesToWarehouse()...");
                    _moveMethod.Invoke(fateObj, null);
                    InfoLog($"[FATE] ({phaseTag}) Ran MoveFloorBoxesToWarehouse().");
                    return true;
                }
                finally
                {
                    if (toggledRunning)
                    {
                        try { _isRunningField?.SetValue(fateObj, false); }
                        catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                WarnLog($"[FATE] ({phaseTag}) Hook failed: {ex.Message}");
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
