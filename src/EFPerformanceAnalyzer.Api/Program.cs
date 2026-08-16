using System.Text;
using EFPerformanceAnalyzer.Api.Options;
using EFPerformanceAnalyzer.Api.Persistence;
using EFPerformanceAnalyzer.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Render (and most PaaS hosts) assign a dynamic port via $PORT and expect the app to bind to it.
var assignedPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(assignedPort))
    builder.WebHost.UseUrls($"http://0.0.0.0:{assignedPort}");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<ScanSettings>(builder.Configuration.GetSection(ScanSettings.SectionName));

// Match the multipart/form parser's limits to the largest upload we'll ever accept
// (RequestSizeLimit on the endpoint enforces the real, configured MaxUploadSizeBytes).
// ValueCountLimit matters specifically for folder uploads: one form part per file, and the
// default of 1024 would truncate any real project (EDAAT alone has 2,500+ .cs files).
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024;
    options.ValueCountLimit = 200_000;
    options.MultipartHeadersCountLimit = 200_000;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024);

// Database:Provider selects SqlServer (default, for local dev against SQL Server Express/full)
// or Postgres (for Render, whose free managed database is Postgres — SQL Server isn't offered
// as a free managed service on most hosts). Render injects the connection as a DATABASE_URL
// postgres:// URI rather than an EF-style connection string, so that gets translated here.
var databaseProvider = builder.Configuration["Database:Provider"] ?? "SqlServer";
builder.Services.AddDbContext<AnalyzerDbContext>(options =>
{
    if (databaseProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(ResolvePostgresConnectionString(builder.Configuration));
    else
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});

builder.Services.AddScoped<ScanTargetValidator>();
builder.Services.AddScoped<AnalysisService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AnalyzerDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Could not reach or initialize the analyzer database. Check ConnectionStrings:DefaultConnection.");
        throw;
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Render terminates TLS at its edge and forwards plain HTTP to the container; redirecting to
// HTTPS again inside the container would loop. Only enforce it when running unproxied (local dev).
if (string.IsNullOrEmpty(assignedPort))
    app.UseHttpsRedirection();

// No accounts, no login page — this is the one gate standing between "reachable on the open
// internet" and "anyone who finds the URL can upload code and browse what's been scanned so far".
// Active only when both env vars are set, so localhost stays exactly as open as before.
var basicAuthUser = builder.Configuration["BASIC_AUTH_USERNAME"];
var basicAuthPass = builder.Configuration["BASIC_AUTH_PASSWORD"];
if (!string.IsNullOrEmpty(basicAuthUser) && !string.IsNullOrEmpty(basicAuthPass))
{
    app.Use(async (context, next) =>
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var parts = decoded.Split(':', 2);
                if (parts.Length == 2 && parts[0] == basicAuthUser && parts[1] == basicAuthPass)
                {
                    await next();
                    return;
                }
            }
            catch (FormatException) { /* malformed header -> fall through to 401 */ }
        }

        context.Response.Headers.WWWAuthenticate = "Basic realm=\"EF Performance Analyzer\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    });
}

// The dashboard is plain static files served by this same app, so `dotnet run` gives you the
// API and the UI on one origin — no second process, no build step, no CORS configuration.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();
app.MapControllers();

app.Run();

static string ResolvePostgresConnectionString(IConfiguration config)
{
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (string.IsNullOrEmpty(databaseUrl))
    {
        return config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("No Postgres connection configured (DATABASE_URL or ConnectionStrings:DefaultConnection).");
    }

    // Render provides DATABASE_URL as postgres://user:password@host:port/database — EF Core's
    // Npgsql provider wants a keyword-value string instead, so translate it.
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':', 2);
    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Require
    };
    return csb.ToString();
}
