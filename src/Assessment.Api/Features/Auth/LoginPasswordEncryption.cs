using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Assessment.Api.Features.Auth;

/// <summary>
/// Application-level encryption of the password field in the login payload (on top of HTTPS).
///
/// The browser fetches a challenge (public key + single-use nonce), encrypts <c>nonce || UTF-8 password</c>
/// with RSA-OAEP-SHA256, and posts only the ciphertext. The server decrypts with its private key and consumes
/// the nonce. A captured payload therefore neither reveals the (possibly reused) password nor can be replayed.
///
/// Limits (DECISIONS.md): does not protect against script running in the page itself, and is not a substitute
/// for TLS. The private key comes from configuration (a secret manager in production); without one, an
/// ephemeral key is generated in memory at startup (Development). Multiple instances would need a shared key
/// and a shared nonce store.
/// </summary>
public sealed class LoginPasswordEncryption : IDisposable
{
    public const string Algorithm = "RSA-OAEP-256";
    public const int NonceBytes = 16;
    public static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(2);
    private const int MaxOutstandingNonces = 10_000;

    private readonly RSA _rsa;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new();

    public string KeyId { get; }
    public string PublicKeySpkiBase64 { get; }

    public LoginPasswordEncryption(IConfiguration configuration, IHostEnvironment environment, TimeProvider clock,
        ILogger<LoginPasswordEncryption> logger)
    {
        _clock = clock;
        _rsa = RSA.Create();

        var pem = configuration["LoginEncryption:PrivateKeyPem"];
        if (!string.IsNullOrWhiteSpace(pem))
        {
            _rsa.ImportFromPem(pem);
        }
        else
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "LoginEncryption:PrivateKeyPem is required outside Development (load it from a secret manager).");
            _rsa.KeySize = 3072; // generates a fresh key, held in memory only
            logger.LogInformation("Using an ephemeral login-encryption key (Development)");
        }

        if (_rsa.KeySize < 3072) throw new InvalidOperationException("Login-encryption key must be at least 3072 bits.");

        var spki = _rsa.ExportSubjectPublicKeyInfo();
        PublicKeySpkiBase64 = Convert.ToBase64String(spki);
        KeyId = Convert.ToHexString(SHA256.HashData(spki))[..16];
    }

    /// <summary>Issues a single-use nonce. Returns null when too many are outstanding (a flood of challenge requests).</summary>
    public string? IssueNonce()
    {
        var now = _clock.GetUtcNow();
        if (_nonces.Count >= MaxOutstandingNonces)
        {
            foreach (var (key, expires) in _nonces)
                if (expires <= now) _nonces.TryRemove(key, out _);
            if (_nonces.Count >= MaxOutstandingNonces) return null;
        }

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceBytes));
        _nonces[nonce] = now + NonceLifetime;
        return nonce;
    }

    /// <summary>
    /// Decrypts the payload and consumes its nonce (atomically: a replayed payload fails even under concurrency).
    /// Returns null for anything invalid: wrong key, tampered or malformed ciphertext, unknown/used/expired nonce.
    /// </summary>
    public string? TryDecryptPassword(string keyId, string ciphertextBase64)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(keyId), Encoding.ASCII.GetBytes(KeyId)))
            return null;

        byte[] plain;
        try
        {
            plain = _rsa.Decrypt(Convert.FromBase64String(ciphertextBase64), RSAEncryptionPadding.OaepSHA256);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }

        try
        {
            if (plain.Length <= NonceBytes) return null;
            var nonce = Convert.ToBase64String(plain, 0, NonceBytes);
            if (!_nonces.TryRemove(nonce, out var expires) || expires <= _clock.GetUtcNow()) return null;
            return Encoding.UTF8.GetString(plain, NonceBytes, plain.Length - NonceBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Dispose() => _rsa.Dispose();
}
