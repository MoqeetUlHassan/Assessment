using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Data;
using Assessment.Api.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Tests.Http;

// Sessions are the front door. What matters: no account enumeration, cookies that scripts and other sites
// can't use, and revocation that takes effect on the NEXT request, not when the cookie expires.
public class AuthenticationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Login_sets_an_httponly_samesite_strict_cookie_and_returns_the_profile()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var client = factory.CreateClient();

        var response = await client.LoginAsync(tenant.Approver.Email.ToUpperInvariant(), TestTenant.Password);

        response.EnsureSuccessStatusCode();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(tenant.Approver.Id, me.GetProperty("userId").GetGuid());
        Assert.Equal(tenant.Org.Id, me.GetProperty("organization").GetProperty("id").GetGuid());
        Assert.Contains("requests.approve", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Wrong_password_unknown_email_and_deactivated_account_are_indistinguishable()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        using (var scope = tenant.AsAdmin(factory))
        {
            var user = await scope.Db.Users.SingleAsync(u => u.Id == tenant.Approver2.Id);
            user.Deactivate(tenant.Admin.Id, DateTimeOffset.UtcNow);
            await scope.Db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        async Task<(HttpStatusCode, string?)> Attempt(string email, string password)
        {
            var r = await client.LoginAsync(email, password);
            return (r.StatusCode, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        }

        var wrongPassword = await Attempt(tenant.Approver.Email, "not-the-password");
        var unknownEmail = await Attempt($"nobody-{Guid.NewGuid():N}@test.local", "not-the-password");
        var deactivated = await Attempt(tenant.Approver2.Email, TestTenant.Password);

        Assert.Equal((HttpStatusCode.Unauthorized, "Invalid email or password."), wrongPassword);
        Assert.Equal(wrongPassword, unknownEmail);
        Assert.Equal(wrongPassword, deactivated);
    }

    [Fact]
    public async Task Unauthenticated_api_calls_get_401_not_a_login_redirect()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Form_encoded_login_is_refused()
    {
        // The shape a cross-site HTML form would send. With SameSite=Strict this is defence in depth.
        var client = factory.CreateClient();
        var response = await client.PostAsync("/api/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "a@b.c", ["password"] = "x" }));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_existing_session_on_the_next_request()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(tenant.Approver);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        await MutateAsync(tenant, db => db.Users.SingleAsync(u => u.Id == tenant.Approver.Id), u => u.Deactivate(tenant.Admin.Id, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Resetting_a_password_ends_existing_sessions()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(tenant.Approver);

        await MutateAsync(tenant, db => db.Users.SingleAsync(u => u.Id == tenant.Approver.Id),
            u => u.ResetPassword(TestTenant.HashPassword(u, "A-Brand-New-Password-1"), tenant.Admin.Id, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Permission_changes_apply_to_existing_sessions_on_the_next_request()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(tenant.Approver);

        await MutateAsync(tenant, db => db.Roles.SingleAsync(r => r.Id == tenant.ApproverRole.Id),
            r => r.SetPermissions([Permissions.RequestsCreate]));

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(["requests.create"], me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(tenant.Requester);

        await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    private async Task MutateAsync<T>(TestTenant tenant, Func<AppDbContext, Task<T>> load, Action<T> change)
    {
        using var scope = tenant.AsAdmin(factory);
        change(await load(scope.Db));
        await scope.Db.SaveChangesAsync();
    }
}

// Separate factory: the real per-IP limit, isolated from the generous limit other tests use.
public class LoginRateLimitTests : IClassFixture<LoginRateLimitTests.LowLimitFactory>
{
    public sealed class LowLimitFactory : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimiting:LoginPermitsPerMinute", "3");
        }
    }

    private readonly LowLimitFactory _factory;
    public LoginRateLimitTests(LowLimitFactory factory) => _factory = factory;

    [Fact]
    public async Task Repeated_login_attempts_are_throttled()
    {
        var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await client.LoginAsync("guess@test.local", $"guess-{i}")).StatusCode);

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests],
            statuses);
    }
}
