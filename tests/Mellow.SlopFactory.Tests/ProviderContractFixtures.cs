namespace Mellow.SlopFactory.Tests;

/// <summary>
/// Versioned, sanitized provider response shapes pinned as named constants rather than scattered
/// inline JSON literals, per plan.md's "provider contract fixtures are versioned so API changes can
/// be reviewed deliberately" rule. Each name carries a version suffix (<c>V1</c>) — a confirmed
/// provider API change gets a new <c>V2</c> constant alongside it (never a silent edit to the
/// existing one) so a reviewer sees the exact shape that changed via a normal diff, and any adapter
/// test still pinned to the old shape keeps failing loudly until it's deliberately updated.
/// Every value here reflects a shape the OpenRouter/DeepInfra adapter research this milestone
/// actually confirmed (see milestone3.md's Provider adapters section for what's confirmed vs.
/// assumed) — this file does not itself invent any new shape.
/// </summary>
internal static class ProviderContractFixtures
{
    public const string OpenRouterImageResponseV1 = """{"data":[{"b64_json":"__BASE64__"}]}""";

    public const string OpenRouterVideoSubmitResponseV1 = """{"id":"__JOB_ID__","polling_url":"__POLLING_URL__","status":"pending"}""";

    public const string OpenRouterVideoPollProcessingV1 = """{"status":"pending"}""";

    public const string OpenRouterVideoPollCompletedV1 = """{"id":"__JOB_ID__","status":"completed","unsigned_urls":["__CONTENT_URL__"]}""";

    public const string OpenRouterVideoPollCompletedWithCostV1 = """{"id":"__JOB_ID__","status":"completed","unsigned_urls":["__CONTENT_URL__"],"usage":{"cost":__COST__,"is_byok":false}}""";

    public const string OpenRouterVideoPollFailedV1 = """{"status":"failed","error":"__ERROR_MESSAGE__"}""";

    public const string DeepInfraChatCompletionResponseV1 = """{"choices":[{"message":{"content":"__CONTENT__"}}]}""";

    public const string DeepInfraImageResponseV1 = """{"data":[{"b64_json":"__BASE64__"}]}""";
}
