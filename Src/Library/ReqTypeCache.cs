﻿using System.Reflection;

namespace FastEndpoints;

internal static class ReqTypeCache<TRequest>
{
    //key: property name
    internal static Dictionary<string, PrimaryPropCacheEntry> CachedProps { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal static PropCacheEntry? CachedFromBodyProp { get; private set; }
    internal static List<SecondaryPropCacheEntry> CachedFromClaimProps { get; } = new();
    internal static List<SecondaryPropCacheEntry> CachedFromHeaderProps { get; } = new();
    internal static List<SecondaryPropCacheEntry> CachedHasPermissionProps { get; } = new();

    static ReqTypeCache()
    {
        var tRequest = typeof(TRequest);

        // if the request dto type is an IEnumerable such as List<T>, it will be deserialized by STJ.
        // so skip caching value parsers for this dto type.
        // otherwise, a request dto such as MyRequest<T> can have a value parser, so allow to proceed.
        if (tRequest.IsGenericType && tRequest.GetInterfaces().Contains(Types.IEnumerable))
            return;

        var isPlainTextRequest = Types.IPlainTextRequest.IsAssignableFrom(tRequest);

        foreach (var propInfo in tRequest.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
        {
            if (!propInfo.CanRead || !propInfo.CanWrite)
                continue;

            if (isPlainTextRequest && propInfo.Name == nameof(IPlainTextRequest.Content))
                continue;

            var compiledSetter = tRequest.SetterForProp(propInfo.Name);

            if (SetFromBodyPropCache(propInfo, compiledSetter))
                continue;

            if (AddFromClaimPropCacheEntry(propInfo, compiledSetter))
                continue;

            if (AddFromHeaderPropCacheEntry(propInfo, compiledSetter))
                continue;

            if (AddHasPermissionPropCacheEntry(propInfo, compiledSetter))
                continue;

            AddPropCacheEntry(propInfo, compiledSetter);
        }
    }

    private static bool SetFromBodyPropCache(PropertyInfo propInfo, Action<object, object> compiledSetter)
    {
        var attrib = propInfo.GetCustomAttribute<FromBodyAttribute>(false);
        if (attrib is not null)
        {
            CachedFromBodyProp = new()
            {
                PropType = propInfo.PropertyType,
                PropSetter = compiledSetter,
            };
            return true;
        }
        return false;
    }

    private static bool AddFromClaimPropCacheEntry(PropertyInfo propInfo, Action<object, object> compiledSetter)
    {
        var attrib = propInfo.GetCustomAttribute<FromClaimAttribute>(false);
        if (attrib is not null)
        {
            var claimType = attrib.ClaimType ?? propInfo.Name;
            var forbidIfMissing = attrib.IsRequired;

            CachedFromClaimProps.Add(new()
            {
                Identifier = claimType,
                ForbidIfMissing = forbidIfMissing,
                PropType = propInfo.PropertyType,
                IsCollection = propInfo.PropertyType != Types.String && propInfo.PropertyType.GetInterfaces().Contains(Types.IEnumerable),
                ValueParser = propInfo.PropertyType.ValueParser(),
                PropSetter = compiledSetter,
            });

            return forbidIfMissing; //if claim is optional, return false so it will also be added as a PropCacheEntry
        }
        return false;
    }

    private static bool AddFromHeaderPropCacheEntry(PropertyInfo propInfo, Action<object, object> compiledSetter)
    {
        var attrib = propInfo.GetCustomAttribute<FromHeaderAttribute>(false);
        if (attrib is not null)
        {
            var headerName = attrib.HeaderName ?? propInfo.Name;
            var forbidIfMissing = attrib.IsRequired;

            CachedFromHeaderProps.Add(new()
            {
                Identifier = headerName,
                ForbidIfMissing = forbidIfMissing,
                PropType = propInfo.PropertyType,
                ValueParser = propInfo.PropertyType.ValueParser(),
                PropSetter = compiledSetter
            });

            return forbidIfMissing; //if header is optional, return false so it will also be added as a PropCacheEntry;
        }
        return false;
    }

    private static bool AddHasPermissionPropCacheEntry(PropertyInfo propInfo, Action<object, object> compiledSetter)
    {
        var attrib = propInfo.GetCustomAttribute<HasPermissionAttribute>(false);
        if (attrib is not null)
        {
            var permission = attrib.Permission ?? propInfo.Name;
            var forbidIfMissing = attrib.IsRequired;

            CachedHasPermissionProps.Add(new()
            {
                Identifier = permission,
                ForbidIfMissing = forbidIfMissing,
                PropType = propInfo.PropertyType,
                PropName = propInfo.Name,
                ValueParser = propInfo.PropertyType.ValueParser(),
                PropSetter = compiledSetter
            });

            return true; // don't allow binding from any other sources
        }
        return false;
    }

    private static void AddPropCacheEntry(PropertyInfo propInfo, Action<object, object> compiledSetter)
    {
        var attrib = propInfo.GetCustomAttribute<BindFromAttribute>(false);

        CachedProps.Add(attrib?.Name ?? propInfo.Name, new()
        {
            PropType = propInfo.PropertyType,
            ValueParser = propInfo.PropertyType.ValueParser(),
            PropSetter = compiledSetter
        });
    }
}

internal class PropCacheEntry
{
    public Type PropType { get; init; }
    public Action<object, object> PropSetter { get; init; }
}

internal class PrimaryPropCacheEntry : PropCacheEntry
{
    public Func<object?, (bool isSuccess, object value)>? ValueParser { get; init; }
}

internal class SecondaryPropCacheEntry : PrimaryPropCacheEntry
{
    public string Identifier { get; init; }
    public bool ForbidIfMissing { get; init; }
    public string? PropName { get; set; }
    public bool IsCollection { get; set; }
}