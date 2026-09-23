using Assessment.Api.Infrastructure.Data;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Assessment.Api.Tests.Persistence;

/// <summary>A DI scope with an explicit tenant state, the same way a real HTTP request gets one.</summary>
internal sealed class DbScope : IDisposable
{
    private readonly IServiceScope _scope;

    private DbScope(IServiceScope scope)
    {
        _scope = scope;
        Tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        Db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    public AppDbContext Db { get; }
    public TenantContext Tenant { get; }

    /// <summary>Seeding / test setup: cross-org writes allowed.</summary>
    public static DbScope System(ApiFactory factory)
    {
        var scope = new DbScope(factory.Services.CreateScope());
        scope.Tenant.UseSystemScope();
        return scope;
    }

    /// <summary>A signed-in user of one organization.</summary>
    public static DbScope As(ApiFactory factory, Guid organizationId, Guid userId)
    {
        var scope = new DbScope(factory.Services.CreateScope());
        scope.Tenant.Set(organizationId, userId);
        return scope;
    }

    /// <summary>No tenant at all, e.g. a code path that forgot to establish one.</summary>
    public static DbScope Anonymous(ApiFactory factory) => new(factory.Services.CreateScope());

    public void Dispose() => _scope.Dispose();
}
