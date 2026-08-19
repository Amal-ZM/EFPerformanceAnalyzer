using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SampleTarget;

public class SecurityPatterns(SampleDbContext context)
{
    // Anti-pattern: SQL injection risk -- interpolated string passed to FromSqlRaw.
    public List<Models.Student> FindByNameRaw(string name)
    {
        return context.Students.FromSqlRaw($"SELECT * FROM Students WHERE Name = '{name}'").ToList();
    }

    // Anti-pattern: string-based Include -- a rename of School won't be caught at compile time.
    public Models.Student? GetWithStringInclude(int id)
    {
        return context.Students.Include("School").FirstOrDefault(s => s.Id == id);
    }

    // Anti-pattern: method has a CancellationToken but doesn't forward it to the async EF call.
    public async Task<List<Models.Student>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await context.Students.ToListAsync();
    }

    // Correct pattern for comparison: token is forwarded.
    public async Task<List<Models.Student>> GetAllAsyncCorrect(CancellationToken cancellationToken)
    {
        return await context.Students.ToListAsync(cancellationToken);
    }
}

public static class StartupMisconfiguration
{
    // Anti-pattern: DbContext registered as a singleton -- not thread-safe, will misbehave under load.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SampleDbContext>();
    }
}
