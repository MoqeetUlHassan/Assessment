using System.Reflection;
using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<MaintenanceRequest> MaintenanceRequests => Set<MaintenanceRequest>();
    public DbSet<RequestRevision> RequestRevisions => Set<RequestRevision>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>
    /// Read through `this` inside query filters, so EF re-evaluates it per context instance (i.e. per request).
    /// Null when no tenant is in scope, which makes every filtered query return nothing: fail closed.
    /// </summary>
    private Guid? CurrentOrganizationId => tenant.OrganizationId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(Entity).IsAssignableFrom(entityType.ClrType))
                modelBuilder.Entity(entityType.ClrType).Ignore(nameof(Entity.PendingAuditEvents));

            // Nothing in this schema cascades: history must never disappear as a side effect.
            foreach (var fk in entityType.GetForeignKeys())
                fk.DeleteBehavior = DeleteBehavior.Restrict;
        }

        ApplyTenantFilters(modelBuilder);
    }

    /// <summary>
    /// Isolation layer 2: every tenant-owned entity is filtered to the caller's organization by default.
    /// The only sanctioned bypass is IgnoreQueryFilters() in the login email lookup (enforced by a test).
    /// </summary>
    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        var apply = typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var clrType in modelBuilder.Model.GetEntityTypes().Select(e => e.ClrType))
        {
            if (typeof(ITenantOwned).IsAssignableFrom(clrType))
                apply.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
        }

        modelBuilder.Entity<Organization>().HasQueryFilter(o => o.Id == CurrentOrganizationId);
    }

    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned =>
        modelBuilder.Entity<T>().HasQueryFilter(e => e.OrganizationId == CurrentOrganizationId);
}
