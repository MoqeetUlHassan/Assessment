using System.Net.Http.Json;
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
}
