using System.Text.RegularExpressions;
using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Tests.Persistence;

// Guards against the NEXT change, not the current code: a new entity added without tenant scoping,
// or a new IgnoreQueryFilters() call quietly reopening cross-tenant reads. Both would pass every other test.
public class TenantModelConventionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Files allowed to bypass tenant filters, and why. Keep this list tiny and justified.</summary>
    private static readonly Dictionary<string, string> IgnoreQueryFiltersAllowList = new()
    {
        // Login must find a user by email before any tenant is known (step 4).
        ["src/Assessment.Api/Features/Auth/LoginEndpoint.cs"] = "email lookup before tenant is known",
    };

    // Call-shaped (".IgnoreQueryFilters("), so prose mentions in docs don't trip it.
    // A commented-out call still does, deliberately: it is one keystroke from live.
    private static readonly Regex IgnoreQueryFiltersCall = new(@"\.\s*IgnoreQueryFilters\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Every_entity_is_tenant_scoped_and_has_a_query_filter()
    {
        using var scope = DbScope.Anonymous(factory);

        foreach (var entityType in scope.Db.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            Assert.True(typeof(ITenantOwned).IsAssignableFrom(clr) || clr == typeof(Organization),
                $"{clr.Name} is neither ITenantOwned nor the Organization tenant root. Every table must be tenant-scoped.");
            Assert.True(entityType.GetDeclaredQueryFilters().Count > 0,
                $"{clr.Name} has no tenant query filter.");
        }
    }

    [Fact]
    public void IgnoreQueryFilters_is_used_only_where_explicitly_allowed()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => IgnoreQueryFiltersCall.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IgnoreQueryFiltersAllowList.ContainsKey(f))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"IgnoreQueryFilters() bypasses tenant isolation. Not allowed in: {string.Join(", ", offenders)}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Assessment.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root (Assessment.slnx) not found.");
    }
}
