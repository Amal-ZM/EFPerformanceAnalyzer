using Microsoft.EntityFrameworkCore;
using SampleTarget.Models;

namespace SampleTarget;

public class StudentDto
{
    public string Name { get; set; } = "";
    public string SchoolName { get; set; } = "";
}

public class StudentService(SampleDbContext context)
{
    // Anti-pattern: N+1 -- School is dereferenced per iteration without Include().
    public List<StudentDto> GetAllWithSchoolNames()
    {
        var students = context.Students.ToList();
        var result = new List<StudentDto>();
        foreach (var s in students)
        {
            result.Add(new StudentDto { Name = s.Name, SchoolName = s.School.Name });
        }
        return result;
    }

    // Anti-pattern: missing AsNoTracking() on a read-only query (no SaveChanges in this method).
    public List<Student> GetAllStudents()
    {
        return context.Students.ToList();
    }

    // Anti-pattern: missing Include() -- School accessed once on a single fetched entity.
    public string GetStudentSchoolName(int id)
    {
        var student = context.Students.FirstOrDefault(s => s.Id == id);
        return student?.School.Name ?? "";
    }

    // Anti-pattern: multiple SaveChanges() calls that should be batched into one.
    public void RenameStudentAndSchool(int studentId, string newStudentName, int schoolId, string newSchoolName)
    {
        var student = context.Students.Find(studentId);
        student!.Name = newStudentName;
        context.SaveChanges();

        var school = context.Schools.Find(schoolId);
        school!.Name = newSchoolName;
        context.SaveChanges();
    }

    // Correct pattern for comparison: eager-loaded, tracked-on-purpose because it writes.
    public void EnrollStudent(int studentId, int courseId)
    {
        var student = context.Students.Include(s => s.Enrollments).FirstOrDefault(s => s.Id == studentId);
        student!.Enrollments.Add(new Enrollment { StudentId = studentId, CourseId = courseId });
        context.SaveChanges();
    }
}
