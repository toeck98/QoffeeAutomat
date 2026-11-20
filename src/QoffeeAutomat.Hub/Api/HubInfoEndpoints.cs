using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Hosting;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Api;

/// <summary>The hub endpoint <c>GET /api/v1/hub</c>: who this hub is (PLAN.md S20, T902). Any signed-in user or API token may read it.</summary>
public static class HubInfoEndpoints
{
    /// <summary>Maps the endpoint.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/hub", GetHub)
            .Describe("Hub", "getHub", "Returns the identity of the hub: id, name, version, the fingerprint of the TLS certificate and the public signing key.")
            .Returns<HubInfo>();
    }

    private static IResult GetHub(
        HubIdentity identity,
        HubSettings settings,
        HubCertificateProvider certificate,
        HubSigningKeyStore signingKey)
    {
        string mode = settings.Tls switch
        {
            HubTlsMode.None => "none",
            HubTlsMode.Certificate => "certificate",
            _ => "selfsigned",
        };
        var info = new HubInfo(identity.Id, identity.Name, HubVersion.Current, mode, certificate.Fingerprint, signingKey.PublicKey);
        return HubApiResults.Json(info, HubApiJsonContext.Default.HubInfo);
    }
}
