using System;
using System.Reflection;
using Mirror;

namespace Bigger_Walk;

public static class MaxConnectionsPatch
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type lobbyModificationSetMaxMembersOptionsType;
    private static MethodInfo setMaxMembersMethod;

    public static int ConfiguredMaxConnections => Math.Max(1, Plugin.MaxConnections.Value);

    public static uint ConfiguredMaxConnectionsUInt => (uint)ConfiguredMaxConnections;

    public static void ApplyToNetworkManager(NetworkManager networkManager)
    {
        if (networkManager != null)
        {
            networkManager.maxConnections = ConfiguredMaxConnections;
        }
    }

    public static void ApplyToEosServer(object server)
    {
        try
        {
            var maxConnectionsProperty = server?.GetType().GetProperty("maxConnections", InstanceFlags);
            if (maxConnectionsProperty?.CanWrite == true && maxConnectionsProperty.PropertyType == typeof(int))
            {
                maxConnectionsProperty.SetValue(server, ConfiguredMaxConnections);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Failed to set EOS server maxConnections " + ex);
        }
    }

    public static void ApplyToLobbyModification(object lobbyModification)
    {
        if (lobbyModification == null)
        {
            return;
        }

        try
        {
            var optionsType = lobbyModificationSetMaxMembersOptionsType ??= Plugin.ResolveKnownType(
                "Epic.OnlineServices.Lobby.LobbyModificationSetMaxMembersOptions"
            );
            if (optionsType == null)
            {
                Plugin.Log.LogWarning("Failed to set EOS lobby MaxMembers: options type not found");
                return;
            }

            var options = Activator.CreateInstance(optionsType);
            SetNumericMember(options, "MaxMembers", ConfiguredMaxConnections);

            var method = setMaxMembersMethod ??= lobbyModification.GetType().GetMethod("SetMaxMembers", InstanceFlags);
            if (method == null)
            {
                Plugin.Log.LogWarning("Failed to set EOS lobby MaxMembers SetMaxMembers method not found");
                return;
            }

            method.Invoke(lobbyModification, new[] { options });
        }
        catch (TargetInvocationException ex)
        {
            Plugin.Log.LogWarning(
                "Failed to set EOS lobby MaxMembers " +
                (ex.InnerException == null ? ex.ToString() : ex.InnerException.ToString())
            );
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Failed to set EOS lobby MaxMembers " + ex);
        }
    }

    private static void SetNumericMember(object target, string memberName, int value)
    {
        var type = target.GetType();
        var field = type.GetField("_" + memberName + "_k__BackingField", InstanceFlags) ??
                    type.GetField(memberName, InstanceFlags);
        if (field != null)
        {
            field.SetValue(target, ConvertNumber(value, field.FieldType));
            return;
        }

        var property = type.GetProperty(memberName, InstanceFlags);
        if (property?.CanWrite == true && property.GetIndexParameters().Length == 0)
        {
            property.SetValue(target, ConvertNumber(value, property.PropertyType));
        }
    }

    private static object ConvertNumber(int value, Type targetType)
    {
        if (targetType == typeof(uint))
        {
            return (uint)Math.Max(0, value);
        }

        if (targetType == typeof(ushort))
        {
            return (ushort)Math.Max(0, value);
        }

        if (targetType == typeof(byte))
        {
            return (byte)Math.Max(0, value);
        }

        if (targetType == typeof(long))
        {
            return (long)value;
        }

        if (targetType == typeof(ulong))
        {
            return (ulong)Math.Max(0, value);
        }

        return value;
    }
}
