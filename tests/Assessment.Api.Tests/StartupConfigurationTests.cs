using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Assessment.Api.Tests;

// A reviewer on a clean machine with a missing connection string should get a message
// pointing at the README, not a NullReferenceException deep inside Npgsql.
public class StartupConfigurationTests
{
    [Fact]
    public void Missing_connection_string_fails_fast_with_actionable_message()
    {
        // Production has no connection string in appsettings.json by design.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Default", ex.ToString());
        Assert.Contains("README", ex.ToString());
    }
}
