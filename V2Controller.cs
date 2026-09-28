using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TranslationSub.Models;
using TranslationSub.Services;

namespace TranslationSub;

/// <summary>
/// Canonical backend-first TranslationSub API consumed by thin Lampa clients.
/// Content identity, progress, source selection, voice matching and mutations
/// are resolved server-side; the browser sends intent and renders read models.
/// </summary>
public class TranslationSubV2Controller : BaseController
{
    [HttpGet]
    [AllowAnonymous]
    [Route("translationsub/v2/snapshot")]
    public ActionResult Snapshot(string uid = null)
    {
        uid = TranslationSubRequestIdentity.ResolveUid(requestInfo, uid);
        if (string.IsNullOrWhiteSpace(uid))
            return Error("uid_required");

        string profileId = TranslationSubRequestIdentity.ResolveProfileId(HttpContext);

        // Server-side safety reconciliation covers restarts or missed NWS
        // invalidations. The client never owns watched-progress synchronization.
        TimeCodeProgressService.SyncUser(uid, profileId);

        return ContentTo(JsonConvert.SerializeObject(
            TranslationSubSnapshotService.Build(uid, profileId)));
    }

    [HttpPost]
    [AllowAnonymous]
    [Route("translationsub/v2/content-state")]
    async public Task<ActionResult> ContentState(string uid = null)
    {
        var body = await ReadBody();
        if (body == null)
            return ContentTo("{\"eligible\":false,\"reason\":\"empty_body\"}");

        uid = TranslationSubRequestIdentity.ResolveUid(requestInfo, uid ?? body.Value<string>("uid"));
        if (string.IsNullOrWhiteSpace(uid))
            return ContentTo("{\"eligible\":false,\"reason\":\"uid_required\"}");

        // Content/voice subscription state is shared user metadata and does not
        // depend on profile-local watched progress. Keep card reads off TimeCode.
        var state = await TranslationSubContentStateService.BuildAsync(
            uid,
            body,
            HttpContext);

        return ContentTo(JsonConvert.SerializeObject(state));
    }

    [HttpPost]
    [AllowAnonymous]
    [Route("translationsub/v2/subscriptions")]
    async public Task<ActionResult> Subscribe(string uid = null)
    {
        var body = await ReadBody();
        if (body == null)
        {
            return ContentTo(JsonConvert.SerializeObject(new TranslationSubCommandResult
            {
                Success = false,
                Error = "empty_body"
            }));
        }

        uid = TranslationSubRequestIdentity.ResolveUid(requestInfo, uid ?? body.Value<string>("uid"));
        string profileId = TranslationSubRequestIdentity.ResolveProfileId(HttpContext);
        var intent = body.ToObject<TranslationSubSubscribeIntent>();
        var result = await TranslationSubCommandService.SubscribeAsync(uid, intent, HttpContext);

        if (result.Success)
        {
            TimeCodeProgressService.SyncUser(uid, profileId);
            result.Snapshot = TranslationSubSnapshotService.Build(uid, profileId);
        }

        return ContentTo(JsonConvert.SerializeObject(result));
    }

    // Dynamic Lampac modules use GET/POST routing. Treat unsubscribe as a
    // command endpoint so the thin client does not rely on an unsupported verb.
    [HttpPost]
    [AllowAnonymous]
    [Route("translationsub/v2/subscriptions/{id}/remove")]
    public ActionResult Unsubscribe(string id, string uid = null)
    {
        uid = TranslationSubRequestIdentity.ResolveUid(requestInfo, uid);
        string profileId = TranslationSubRequestIdentity.ResolveProfileId(HttpContext);
        var result = TranslationSubCommandService.Unsubscribe(uid, id);

        if (result.Success)
        {
            TimeCodeProgressService.SyncUser(uid, profileId);
            result.Snapshot = TranslationSubSnapshotService.Build(uid, profileId);
        }

        return ContentTo(JsonConvert.SerializeObject(result));
    }

    [HttpPost]
    [AllowAnonymous]
    [Route("translationsub/v2/check")]
    async public Task<ActionResult> Check(string uid = null)
    {
        uid = TranslationSubRequestIdentity.ResolveUid(requestInfo, uid);
        if (string.IsNullOrWhiteSpace(uid))
            return Error("uid_required");

        string profileId = TranslationSubRequestIdentity.ResolveProfileId(HttpContext);
        TimeCodeProgressService.SyncUser(uid, profileId);

        await TranslationSubscriptionService.Tick(uid, force: true);

        TimeCodeProgressService.SyncUser(uid, profileId);
        var snapshot = TranslationSubSnapshotService.Build(uid, profileId);

        return ContentTo(JsonConvert.SerializeObject(new
        {
            success = true,
            snapshot
        }));
    }

    ActionResult Error(string error)
        => ContentTo(JsonConvert.SerializeObject(new
        {
            success = false,
            error
        }));

    async Task<JObject> ReadBody()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        string raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<JObject>(raw);
        }
        catch
        {
            return null;
        }
    }
}
