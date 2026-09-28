using Shared.Models.Base;
using Shared.Models.Module.Entrys;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TranslationSub.Models;

namespace TranslationSub.Services;

internal static class LampacSourceRegistry
{
    public static IReadOnlyList<LampacSourceOption> AvailableSources()
    {
        OnlineModuleEntry.EnsureCache();

        var result = new Dictionary<string, LampacSourceOption>(StringComparer.OrdinalIgnoreCase);
        AddAvailableModules(OnlineModuleEntry.Modules?.Cast<object>(), result);
        AddAvailableModules(OnlineModuleEntry.ModulesAsync?.Cast<object>(), result);

        return result.Values
            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<LampacSourceDescriptor> ResolveSelected(IEnumerable<string> sourceIds)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourceIds != null)
        {
            foreach (string sourceId in sourceIds)
            {
                string id = TranslationSettingsStore.NormalizeSourceId(sourceId);
                if (id != null)
                    selected.Add(id);
            }
        }

        if (selected.Count == 0)
            return Array.Empty<LampacSourceDescriptor>();

        var routes = CollectSelectedSources(selected);
        return routes.Values
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static void AddAvailableModules(
        IEnumerable<object> modules,
        Dictionary<string, LampacSourceOption> result)
    {
        if (modules == null)
            return;

        foreach (object module in modules)
        {
            if (module == null)
                continue;

            foreach (BaseSettings settings in FindSettings(module))
            {
                if (settings == null || !CanProduceOnlineSource(settings))
                    continue;

                string id = TranslationSettingsStore.NormalizeSourceId(settings.plugin);
                if (id == null)
                    continue;

                string name = SourceName(settings, id);
                result[id] = new LampacSourceOption
                {
                    id = id,
                    name = name
                };
            }
        }
    }

    static Dictionary<string, LampacSourceDescriptor> CollectSelectedSources(HashSet<string> selected)
    {
        OnlineModuleEntry.EnsureCache();

        var result = new Dictionary<string, LampacSourceDescriptor>(StringComparer.OrdinalIgnoreCase);
        AddSelectedModules(OnlineModuleEntry.Modules?.Cast<object>(), result, selected);
        AddSelectedModules(OnlineModuleEntry.ModulesAsync?.Cast<object>(), result, selected);
        return result;
    }

    static void AddSelectedModules(
        IEnumerable<object> modules,
        Dictionary<string, LampacSourceDescriptor> result,
        HashSet<string> selected)
    {
        if (modules == null)
            return;

        foreach (object module in modules)
        {
            if (module == null)
                continue;

            foreach (BaseSettings settings in FindSettings(module))
            {
                if (settings == null)
                    continue;

                string id = TranslationSettingsStore.NormalizeSourceId(settings.plugin);
                if (id == null || !selected.Contains(id))
                    continue;

                string route = EffectiveRoute(settings, id);
                if (string.IsNullOrWhiteSpace(route))
                    continue;

                result[id] = new LampacSourceDescriptor
                {
                    Id = id,
                    Name = SourceName(settings, id),
                    Url = route
                };
            }
        }
    }

    static bool CanProduceOnlineSource(BaseSettings settings)
    {
        if (settings.enable && !settings.rip)
            return true;

        // Mirror OnlineApi.send(): a configured remote override can remain usable
        // even when the local provider is disabled/rip, unless overridepasswd
        // intentionally suppresses the override route.
        if (!string.IsNullOrEmpty(settings.overridepasswd))
            return false;

        if (!string.IsNullOrWhiteSpace(settings.overridehost))
            return true;

        return settings.overridehosts?.Any(x => !string.IsNullOrWhiteSpace(x)) == true;
    }

    static string EffectiveRoute(BaseSettings settings, string id)
    {
        // Mirror OnlineApi.send() route precedence without invoking the provider:
        // a remote override wins when overridepasswd is not set; otherwise an
        // enabled local module is addressed through its normal /lite/{plugin} route.
        if (string.IsNullOrEmpty(settings.overridepasswd))
        {
            if (!string.IsNullOrWhiteSpace(settings.overridehost))
                return settings.overridehost.Trim();

            string[] hosts = settings.overridehosts?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToArray();
            if (hosts?.Length > 0)
                return hosts[Random.Shared.Next(0, hosts.Length)];
        }

        if (settings.enable && !settings.rip)
            return "/lite/" + id;

        return null;
    }

    static string SourceName(BaseSettings settings, string id)
    {
        string name = settings.displayname;
        if (string.IsNullOrWhiteSpace(name))
            name = settings.plugin;
        if (string.IsNullOrWhiteSpace(name))
            name = id;

        return name.Trim();
    }

    static IEnumerable<BaseSettings> FindSettings(object module)
    {
        Type type = module.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        var seen = new HashSet<BaseSettings>(ReferenceEqualityComparer.Instance);

        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (!typeof(BaseSettings).IsAssignableFrom(field.FieldType))
                continue;

            BaseSettings value = null;
            try { value = field.GetValue(field.IsStatic ? null : module) as BaseSettings; }
            catch { }

            if (value != null && seen.Add(value))
                yield return value;
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0
                || !typeof(BaseSettings).IsAssignableFrom(property.PropertyType))
                continue;

            MethodInfo getter = property.GetGetMethod(true);
            if (getter == null)
                continue;

            BaseSettings value = null;
            try { value = property.GetValue(getter.IsStatic ? null : module) as BaseSettings; }
            catch { }

            if (value != null && seen.Add(value))
                yield return value;
        }
    }
}
