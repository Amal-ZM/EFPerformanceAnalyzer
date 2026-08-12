using System.Collections.Generic;

namespace SampleTarget.Models;

public class Student
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SchoolId { get; set; }
    public School School { get; set; } = null!;
    public List<Enrollment> Enrollments { get; } = [];
}

public class School
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DistrictId { get; set; }
    public District District { get; set; } = null!;
    public List<Student> Students { get; } = [];
}

public class District
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string RegionCode { get; set; } = "";
}

public class Course
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

public class Enrollment
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
}
