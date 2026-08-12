using Microsoft.EntityFrameworkCore;
using SampleTarget.Models;

namespace SampleTarget;

public class SampleDbContext : DbContext
{
    public DbSet<Student> Students => Set<Student>();
    public DbSet<School> Schools => Set<School>();
    public DbSet<District> Districts => Set<District>();
    public DbSet<Course> Courses => Set<Course>();
}
