using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;

namespace Assessment.Api.Tests.Http;

// Production behaviour that Development deliberately relaxes (plain HTTP, ephemeral login key, seeding).
// If these regress, nothing else in the suite notices, because every other test runs as Development.
public class ProductionModeTests : IClassFixture<ProductionModeTests.ProductionFactory>
{
    public sealed class ProductionFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            using var rsa = RSA.Create(3072);
            builder.UseEnvironment("Production");
            builder.UseSetting("https_port", "443");
            builder.UseSetting("LoginEncryption:PrivateKeyPem", rsa.ExportRSAPrivateKeyPem()); // from a secret manager in real life
            builder.UseSetting("Seed:DevelopmentData", "false");
        }
    }

    private readonly ProductionFactory _factory;
    public ProductionModeTests(ProductionFactory factory) => _factory = factory;

    [Fact]
    public async Task Plain_http_is_redirected_to_https()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });

        var response = await client.GetAsync("/api/auth/password-challenge");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https", response.Headers.Location?.Scheme);
    }

    [Fact]
    public async Task Https_responses_carry_hsts()
    {
        // A real host name: ASP.NET Core's HSTS middleware deliberately never sends HSTS for localhost,
        // so a developer's browser isn't pinned to HTTPS for every localhost app.
        var client = _factory.CreateClient(new() { BaseAddress = new Uri("https://maintenance.example.com") });

        var response = await client.GetAsync("/api/auth/password-challenge");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age=", string.Join(";", response.Headers.GetValues("Strict-Transport-Security")));
    }

    [Fact]
    public void Startup_fails_without_a_configured_login_encryption_key()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("LoginEncryption:PrivateKeyPem", ex.ToString());
    }
}
