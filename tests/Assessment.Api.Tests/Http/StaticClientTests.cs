using System.Net;

namespace Assessment.Api.Tests.Http;

// The CSP is what makes an XSS mistake in the client non-exploitable (no inline script runs). A header
// silently dropped by a middleware reorder would not break any functional test, so it is pinned here.
public class StaticClientTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/requests.html")]
    [InlineData("/api/me")] // API responses too, not just pages
    public async Task Every_response_carries_a_strict_content_security_policy(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Pages_are_public_but_contain_no_data()
    {
        // Anonymous users get the page shell; every piece of data comes from the authenticated API.
        var response = await factory.CreateClient().GetAsync("/requests.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
