using EFPerformanceAnalyzer.Api.Options;
using EFPerformanceAnalyzer.Api.Persistence;
using EFPerformanceAnalyzer.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddDbContext<AnalyzerDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

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

app.UseHttpsRedirection();

// The dashboard is plain static files served by this same app, so `dotnet run` gives you the
// API and the UI on one origin — no second process, no build step, no CORS configuration.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();
app.MapControllers();

app.Run();
