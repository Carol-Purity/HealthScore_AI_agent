using Microsoft.PowerPlatform.Dataverse.Client;

namespace AccountHealthScoreFunction;

// One place that builds the Dataverse connection from app settings (client-secret auth as an application user).
public static class DataverseConnection
{
    public static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    public static ServiceClient Create()
    {
        string url = Environment.GetEnvironmentVariable("DataverseUrl") ?? "";
        string clientId = Environment.GetEnvironmentVariable("DataverseClientId") ?? "";
        string clientSecret = Environment.GetEnvironmentVariable("DataverseClientSecret") ?? "";
        string tenantId = Environment.GetEnvironmentVariable("DataverseTenantId") ?? "";

        return new ServiceClient(
            $"AuthType=ClientSecret;Url={url};ClientId={clientId};ClientSecret={clientSecret};TenantId={tenantId};");
    }
}
