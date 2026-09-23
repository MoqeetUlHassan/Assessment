using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assessment.Api.Tests.Http;

/// <summary>Builds a login body exactly as the browser does: challenge → RSA-OAEP-SHA256(nonce || password).</summary>
internal static class LoginPayload
{
    public sealed record Challenge(string KeyId, string PublicKey, string Nonce);

    public static async Task<Challenge> GetChallengeAsync(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/auth/login-challenge");
        return new Challenge(json.GetProperty("keyId").GetString()!, json.GetProperty("publicKey").GetString()!,
            json.GetProperty("nonce").GetString()!);
    }

    public static string Encrypt(Challenge challenge, string password, string? publicKeyOverride = null)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyOverride ?? challenge.PublicKey), out _);
        var plain = Convert.FromBase64String(challenge.Nonce).Concat(Encoding.UTF8.GetBytes(password)).ToArray();
        return Convert.ToBase64String(rsa.Encrypt(plain, RSAEncryptionPadding.OaepSHA256));
    }

    public static async Task<object> CreateAsync(HttpClient client, string email, string password)
    {
        var challenge = await GetChallengeAsync(client);
        return new { email, keyId = challenge.KeyId, encryptedPassword = Encrypt(challenge, password) };
    }

    public static async Task<HttpResponseMessage> LoginAsync(this HttpClient client, string email, string password) =>
        await client.PostAsJsonAsync("/api/auth/login", await CreateAsync(client, email, password));
}
