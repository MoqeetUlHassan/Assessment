using System.Threading.RateLimiting;
using Assessment.Api.Authorization;
using Assessment.Api.Domain;
using Assessment.Api.Features.Admin;
using Assessment.Api.Features.Auth;
using Assessment.Api.Features.Reports;
using Assessment.Api.Features.Requests;
using Assessment.Api.Features.Sites;
using Assessment.Api.Infrastructure;
using Assessment.Api.Infrastructure.Data;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Default' is missing. See README.md.");

// --- Data ---
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

// --- Authentication: HttpOnly session cookie; API-style 401/403 instead of redirects ---
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "mr_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict; // with JSON-only mutations and no CORS: CSRF defence
        // Development runs over plain HTTP (no dev-cert step for reviewers); everywhere else is HTTPS-only.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<PasswordFieldEncryption>(); // RSA key + single-use nonces for password fields

// --- Authorization: permission policies + resource handlers, fed by CurrentUser (loaded per request) ---
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddScoped<IAuthorizationHandler, MaintenanceRequestAuthorizationHandler>();
builder.Services.AddAuthorizationBuilder().AddPermissionPolicies();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, PermissionDeniedResultHandler>(); // 403 names the missing permission

// --- Rate limiting: login attempts per client IP ---
var loginPermits = builder.Configuration.GetValue("RateLimiting:LoginPermitsPerMinute", 10);
var challengePermits = builder.Configuration.GetValue("RateLimiting:LoginChallengePermitsPerMinute", 30);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(LoginEndpoint.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPermits, Window = TimeSpan.FromMinutes(1) }));
    // Challenges are cheap but hold a nonce for 2 minutes each: limited separately so they can't flood memory.
    options.AddPolicy(LoginEndpoint.ChallengeRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = challengePermits, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddValidation();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Fail fast: outside Development a missing login-encryption key must stop startup, not the first login.
_ = app.Services.GetRequiredService<PasswordFieldEncryption>();

// Apply pending migrations on startup so a fresh clone runs with one command.
// Off by default outside Development; see DECISIONS.md.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await DatabaseMigrator.MigrateAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>()); // advisory-locked
}

if (app.Configuration.GetValue<bool>("Seed:DevelopmentData"))
{
    await DevelopmentSeeder.SeedAsync(app.Services);
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// Outside Development every request must be HTTPS: redirect plain HTTP, and HSTS tells browsers never to
// try HTTP again. (Development stays on plain HTTP so reviewers need no dev-certificate step.)
// Behind a TLS-terminating proxy, also configure forwarded headers so the original scheme is known.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Security headers on every response. The CSP allows only same-origin scripts/styles (no inline script),
// so even if user text were ever rendered as HTML by mistake, injected script would not execute.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers.XFrameOptions = "DENY";
    await next();
});

// Static client (wwwroot): public files only; all data goes through the authenticated API.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<SessionValidationMiddleware>(); // tenant + fresh user/permissions, before any authorization
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");
app.MapLogin();
app.MapSession();
app.MapSites();
app.MapRequests();
app.MapAdmin();
app.MapSpendReport();

app.Run();

// Exposed for WebApplicationFactory<Program> in the test project.
public partial class Program;
