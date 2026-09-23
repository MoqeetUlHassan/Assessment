using System.Net.Http.Json;
using System.Text.Json;
using Assessment.Api.Domain;
using Assessment.Api.Tests.Persistence;

namespace Assessment.Api.Tests.Http;

internal static class HttpTestExtensions
{
    /// <summary>A fresh client with its own cookie jar, signed in as the given user.</summary>
    public static async Task<HttpClient> SignedInClientAsync(this ApiFactory factory, User user,
        string password = TestTenant.Password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public static async Task<JsonElement> CreateRequestAsync(this HttpClient client, Guid siteId, decimal estimate,
        string description = "Fix boiler")
    {
        var response = await client.PostAsJsonAsync("/api/requests", new { siteId, description, estimatedCost = estimate });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> ApproveAsync(this HttpClient client, Guid requestId, Guid revisionId) =>
        client.PostAsJsonAsync($"/api/requests/{requestId}/approve", new { revisionId });

    public static Task<HttpResponseMessage> RejectAsync(this HttpClient client, Guid requestId, Guid revisionId) =>
        client.PostAsJsonAsync($"/api/requests/{requestId}/reject", new { revisionId, reason = "Not justified" });

    public static Guid Id(this JsonElement request) => request.GetProperty("id").GetGuid();

    public static Guid PendingRevisionId(this JsonElement request) =>
        request.GetProperty("pendingRevision").GetProperty("id").GetGuid();

    public static string Status(this JsonElement request) => request.GetProperty("status").GetString()!;

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
