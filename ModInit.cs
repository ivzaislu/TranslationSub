using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Newtonsoft.Json;
using Shared.Models.Base;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;
using Shared.Services;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TranslationSub.Services;

namespace TranslationSub;

public class ModInit : IModuleLoaded
{
    public static ModuleConf conf;
    public static string modpath;

    public void Loaded(InitspaceModel initspace)
    {
        modpath = initspace.path;

        updateConf();
        EventListener.UpdateInitFile += updateConf;
        EventListener.AppReplace += appReplace;
        EventListener.NwsMessage += nwsMessage;
        EventListener.NwsDisconnected += nwsDisconnected;
        EventListener.Middleware += middleware;
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
        EventListener.AppReplace -= appReplace;
        EventListener.NwsMessage -= nwsMessage;
        EventListener.NwsDisconnected -= nwsDisconnected;
        EventListener.Middleware -= middleware;
        TranslationSubscriptionService.Stop();
    }

    static void updateConf()
    {
        conf = ModuleInvoke.Init("TranslationSub", new ModuleConf());

        if (conf?.enable == true)
            TranslationSubscriptionService.Start();
        else
            TranslationSubscriptionService.Stop();
    }

    static void nwsMessage(EventNwsMessage e)
    {
        if (conf?.enable != true || e == null
            || !string.Equals(e.method, "TranslationSubRegister", StringComparison.Ordinal)
            || e.args.ValueKind != JsonValueKind.Array
            || e.args.GetArrayLength() < 1)
            return;

        try
        {
            string uid = e.args[0].ToString();
            string profileId = e.args.GetArrayLength() > 1 ? e.args[1].ToString() : "0";
            TranslationSubRealtimeService.Register(e.connectionId, uid, profileId);
        }
        catch { }
    }

    static void nwsDisconnected(EventNwsDisconnected e)
    {
        if (e != null)
            TranslationSubRealtimeService.Unregister(e.connectionId);
    }

    static bool middleware(bool first, EventMiddleware e)
    {
        if (first || conf?.enable != true || e?.httpContext == null)
            return true;

        var context = e.httpContext;
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method)
            || !string.Equals(request.Path.Value, "/timecode/add", StringComparison.OrdinalIgnoreCase))
            return true;

        var requestInfo = context.Features.Get<RequestModel>();
        string uid = requestInfo?.user_uid?.Trim();
        if (string.IsNullOrWhiteSpace(uid))
            return true;

        string profileId = request.Query.TryGetValue("profile_id", out var profileQuery)
            ? profileQuery.ToString()
            : "0";

        context.Response.OnCompleted(() =>
        {
            if (context.Response.StatusCode < 200 || context.Response.StatusCode >= 300)
                return Task.CompletedTask;

            int changed = TimeCodeProgressService.SyncUser(uid, profileId);
            return changed > 0
                ? TranslationSubRealtimeService.PublishProfile(uid, profileId, "timecode")
                : Task.CompletedTask;
        });

        return true;
    }

    static StringBuilder appReplace(string type, EventAppReplace e)
    {
        if (type != "appjs" || conf?.enable != true)
            return e.bulder;

        string pluginUrl = JsonConvert.SerializeObject($"{e.host.TrimEnd('/')}/translationsub.js");

        e.bulder.Append($@"
;(function(){{
    if (window.__TranslationSubAutoLoad) return;
    window.__TranslationSubAutoLoad = true;

    try {{
        if (document.querySelector('script[data-plugin=""translationsub""]')) return;

        var script = document.createElement('script');
        script.src = {pluginUrl};
        script.async = true;
        script.setAttribute('data-plugin', 'translationsub');
        (document.head || document.documentElement).appendChild(script);
    }} catch (e) {{
        try {{ console.log('[TranslationSub] autoload failed', e); }} catch (_) {{}}
    }}
}})();");

        return e.bulder;
    }
}
