using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SampleTarget.Models;

namespace SampleTarget;

/// <summary>
/// Companion to <see cref="StudentService"/>: one deliberate instance of each anti-pattern the
/// query-shape and general-throughput detectors look for. Re-scan this project after changing a
/// detector to confirm it still fires exactly where it should.
/// </summary>
public class ReportingService(SampleDbContext context)
{
    // Anti-pattern: client-side evaluation -- ToList() materializes every Student row, then
    // Where() filters them in memory instead of in the SQL WHERE clause.
    public List<Student> FindStudentsByPrefix(string prefix)
    {
        return context.Students.ToList().Where(s => s.Name.StartsWith(prefix)).ToList();
    }

    // Anti-pattern: query inside a loop -- one round trip per id instead of a single batched query.
    public List<string> GetNamesByIds(List<int> ids)
    {
        var names = new List<string>();
        foreach (var id in ids)
        {
            var student = context.Students.FirstOrDefault(s => s.Id == id);
            names.Add(student?.Name ?? "");
        }
        return names;
    }

    // Anti-pattern: SaveChanges() inside a loop -- one transaction per row rather than one batch.
    public void ArchiveAll(List<Student> students)
    {
        foreach (var student in students)
        {
            student.Name = "[archived] " + student.Name;
            context.SaveChanges();
        }
    }

    // Anti-pattern: unbounded query -- no Where filter and no paging, so this loads the whole table.
    public List<Course> GetEveryCourse()
    {
        return context.Courses.ToList();
    }

    // Anti-pattern: cartesian Include -- three stacked Includes in one JOIN-ed statement, so rows
    // multiply across the included collections.
    public List<Student> GetStudentRoster(int schoolId)
    {
        return context.Students
            .Where(s => s.SchoolId == schoolId)
            .Include(s => s.School)
            .ThenInclude(sc => sc.District)
            .Include(s => s.Enrollments)
            .ToList();
    }

    // Anti-pattern: Count() used as an existence check -- SELECT COUNT(*) where EXISTS would do.
    public bool HasAnyStudents()
    {
        return context.Students.Count() > 0;
    }

    // Anti-pattern: sync-over-async -- .Result blocks a thread-pool thread until the task completes.
    public List<Student> LoadStudentsBlocking(int schoolId)
    {
        return LoadStudentsAsync(schoolId).Result;
    }

    private async Task<List<Student>> LoadStudentsAsync(int schoolId)
    {
        return await context.Students.Where(s => s.SchoolId == schoolId).ToListAsync();
    }

    // Anti-pattern: async void -- the caller gets no Task, so it can neither await this nor catch
    // anything it throws.
    public async void RefreshCache()
    {
        await Task.Delay(10);
    }

    // Anti-pattern: string += inside a loop -- reallocates and copies the accumulated string on
    // every iteration, making the loop quadratic.
    public string BuildNameCsv(List<Student> students)
    {
        var csv = "";
        foreach (var student in students)
        {
            csv += student.Name + ",";
        }
        return csv;
    }

    // Anti-pattern: blocking call in an async method -- Thread.Sleep parks the thread instead of
    // yielding it back to the pool.
    public async Task<int> SyncCoursesAsync()
    {
        Thread.Sleep(500);
        return await context.Courses.Where(c => c.Title != "").CountAsync();
    }

    // Correct pattern for comparison: filtered, paged, read-only, and awaited properly.
    public async Task<List<Student>> GetPageAsync(int schoolId, int skip, int take)
    {
        return await context.Students
            .AsNoTracking()
            .Where(s => s.SchoolId == schoolId)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
    }
}
