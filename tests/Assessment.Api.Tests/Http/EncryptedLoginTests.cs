using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Assessment.Api.Tests.Persistence;

namespace Assessment.Api.Tests.Http;

// The password field is encrypted in the login payload. What must hold for that to mean anything:
// plain passwords are no longer accepted, a captured payload cannot be replayed, and anything not
// produced with the server's current key and a fresh challenge is refused before any account lookup.
public class EncryptedLoginTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_plain_text_password_field_is_not_accepted()
    {
        var t = await TestTenant.CreateAsync(factory);
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = t.Approver.Email, password = TestTenant.Password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_captured_login_payload_cannot_be_replayed()
    {
        var t = await TestTenant.CreateAsync(factory);
        var client = factory.CreateClient();
        var captured = await PasswordPayload.CreateAsync(client, t.Approver.Email, TestTenant.Password);

        var first = await client.PostAsJsonAsync("/api/auth/login", captured);
        var replay = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", captured);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode); // nonce already consumed
    }

    [Fact]
    public async Task Ciphertext_from_another_key_a_wrong_key_id_or_tampering_is_refused()
    {
        var t = await TestTenant.CreateAsync(factory);
        var client = factory.CreateClient();

        using var attackerKey = RSA.Create(3072);
        var foreignPublicKey = Convert.ToBase64String(attackerKey.ExportSubjectPublicKeyInfo());
        var c1 = await PasswordPayload.GetChallengeAsync(client);
        var wrongKey = new { email = t.Approver.Email, keyId = c1.KeyId, encryptedPassword = PasswordPayload.Encrypt(c1, TestTenant.Password, foreignPublicKey) };

        var c2 = await PasswordPayload.GetChallengeAsync(client);
        var wrongKeyId = new { email = t.Approver.Email, keyId = "0000000000000000", encryptedPassword = PasswordPayload.Encrypt(c2, TestTenant.Password) };

        var c3 = await PasswordPayload.GetChallengeAsync(client);
        var bytes = Convert.FromBase64String(PasswordPayload.Encrypt(c3, TestTenant.Password));
        bytes[^1] ^= 0x01;
        var tampered = new { email = t.Approver.Email, keyId = c3.KeyId, encryptedPassword = Convert.ToBase64String(bytes) };

        foreach (var body in new object[] { wrongKey, wrongKeyId, tampered })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login", body)).StatusCode);
    }

    [Fact]
    public async Task Each_challenge_has_a_fresh_nonce_under_the_same_key()
    {
        var client = factory.CreateClient();
        var a = await PasswordPayload.GetChallengeAsync(client);
        var b = await PasswordPayload.GetChallengeAsync(client);

        Assert.Equal(a.KeyId, b.KeyId);
        Assert.NotEqual(a.Nonce, b.Nonce);
    }
}
