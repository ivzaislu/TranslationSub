using Microsoft.AspNetCore.Http;
using Shared.Models.Base;

namespace TranslationSub.Services;

static class TranslationSubRequestIdentity
{
    public static string ResolveUid(RequestModel requestInfo, string explicitUid = null)
    {
        string fromKernel = requestInfo?.user_uid?.Trim();
        if (!string.IsNullOrWhiteSpace(fromKernel))
            return fromKernel;

        string fromCaller = explicitUid?.Trim();
        if (!string.IsNullOrWhiteSpace(fromCaller))
            return fromCaller;

        return null;
    }

    public static string ResolveProfileId(HttpContext http)
    {
        if (http?.Request?.Query.TryGetValue("profile_id", out var profileQuery) == true)
            return ProfileProgressStore.NormalizeProfileId(profileQuery.ToString());

        return "0";
    }
}
