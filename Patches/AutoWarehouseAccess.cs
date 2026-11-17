#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Knarf.AwCompat
{
    // This is an optional adapter to the private layout in AW 2.0.2, not a hard dependency.
    internal static class AutoWarehouseAccess
    {
        internal const string Guid = "ivand.automaticwarehouse";
        private static Type? _type;

        internal static bool Installed => Chainloader.PluginInfos.ContainsKey(Guid);

        internal static object? Open()
        {
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) ||
                info.Instance == null)
                return null;

            var version = info.Metadata.Version;
            if (version.Major != 2 || version.Minor != 0 || version.Build != 2)
                throw new NotSupportedException($"Auto Warehouse {version}; this bridge targets 2.0.2.");

            _type = info.Instance.GetType();
            return info.Instance;
        }

        private static readonly Dictionary<(Type, string), FieldInfo> Fields = new();
        private static readonly Dictionary<(Type, string), MethodInfo> Methods = new();

        internal static FieldInfo Field(Type type, string name)
        {
            var key = (type, name);
            if (!Fields.TryGetValue(key, out var field))
            {
                field = AccessTools.Field(type, name) ??
                    throw new MissingFieldException(type.FullName, name);
                Fields.Add(key, field);
            }
            return field;
        }

        internal static object? Read(object instance, string name) =>
            Field(instance.GetType(), name).GetValue(instance);

        internal static IDictionary Map(object aw, string name) =>
            (IDictionary)(Read(aw, name) ?? throw new InvalidOperationException(name));

        internal static bool Flag(object instance, string name) => (bool)Read(instance, name)!;

        internal static MethodInfo Method(Type type, string name)
        {
            var key = (type, name);
            if (!Methods.TryGetValue(key, out var method))
            {
                method = AccessTools.Method(type, name) ??
                    throw new MissingMethodException(type.FullName, name);
                Methods.Add(key, method);
            }
            return method;
        }

        internal static object? Call(object aw, string name, params object?[] args) =>
            Method(aw.GetType(), name).Invoke(aw, args);

        internal static bool ValidMachine(object? machine) =>
            machine != null && (bool)Method(_type!, "Valid").Invoke(null, new[] { machine })!;

        internal static bool Busy(object aw) =>
            !Flag(aw, "ready") || Flag(aw, "quitting") ||
            Flag(aw, "warehouseRestoreIncomplete") || Flag(aw, "stockDeleteBusy") ||
            Flag(aw, "collectLooseBusy") || Read(aw, "pending") != null ||
            (bool)Call(aw, "get_CoopClient")!;

        internal static bool CanDeliverDirectly(object aw) =>
            Read(aw, "directPurchases") is BepInEx.Configuration.ConfigEntry<bool> entry &&
            entry.Value;
    }
}
