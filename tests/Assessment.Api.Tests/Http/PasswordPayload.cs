using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Assessment.Api.Tests.Http;

/// <summary>Encrypts a password field exactly as the browser does: challenge → RSA-OAEP-SHA256(nonce || password).</summary>
internal static class PasswordPayload
{
    public sealed record Challenge(string KeyId, string PublicKey, string Nonce);

    public static async Task<Challenge> GetChallengeAsync(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/auth/password-challenge");
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

    /// <summary>A fresh challenge and the encrypted password, for any endpoint with a password field.</summary>
    public static async Task<(string KeyId, string EncryptedPassword)> EncryptAsync(HttpClient client, string password)
    {
        var challenge = await GetChallengeAsync(client);
        return (challenge.KeyId, Encrypt(challenge, password));
    }

    public static async Task<object> CreateAsync(HttpClient client, string email, string password)
    {
        var (keyId, encryptedPassword) = await EncryptAsync(client, password);
        return new { email, keyId, encryptedPassword };
    }

    public static async Task<HttpResponseMessage> LoginAsync(this HttpClient client, string email, string password) =>
        await client.PostAsJsonAsync("/api/auth/login", await CreateAsync(client, email, password));
}
