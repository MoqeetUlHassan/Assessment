using Assessment.Api.Infrastructure.Data;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Default' is missing. See README.md.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<AuditEventInterceptor>();
builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<EntityStampingInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention()
    // Order matters: collect audit rows first so the tenant guard checks them too; stamp last.
    .AddInterceptors(
        sp.GetRequiredService<AuditEventInterceptor>(),
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<EntityStampingInterceptor>()));
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

// Apply pending migrations on startup so a fresh clone runs with one command.
// Off by default outside Development; see DECISIONS.md.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

app.Run();

// Exposed for WebApplicationFactory<Program> in the test project.
public partial class Program;
