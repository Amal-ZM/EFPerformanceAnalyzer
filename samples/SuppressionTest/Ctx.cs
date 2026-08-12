using Microsoft.EntityFrameworkCore;

namespace SuppressionTest;

public class Widget { public int Id { get; set; } public string Name { get; set; } = ""; }

public class Ctx : DbContext
{
    public DbSet<Widget> Widgets => Set<Widget>();
}

public class WidgetService(Ctx context)
{
    public List<Widget> GetAllSuppressed()
    {
        return context.Widgets.ToList(); // ef-analyzer-ignore: MissingAsNoTracking
    }

    // fixed: now uses AsNoTracking
    public List<Widget> GetAllNotSuppressed()
    {
        return context.Widgets.AsNoTracking().ToList();
    }

    // new issue introduced: multiple SaveChanges in a loop
    public void ResetAll(List<Widget> widgets)
    {
        foreach (var w in widgets)
        {
            context.Widgets.Update(w);
            context.SaveChanges();
        }
    }
}
