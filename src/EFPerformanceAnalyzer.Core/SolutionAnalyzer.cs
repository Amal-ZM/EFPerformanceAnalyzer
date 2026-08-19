using EFPerformanceAnalyzer.Core.Detectors;
using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using EFPerformanceAnalyzer.Core.Suppression;
using Microsoft.CodeAnalysis.CSharp;

namespace EFPerformanceAnalyzer.Core;

public sealed class SolutionAnalyzer
{
    private static readonly string[] ExcludedDirectoryNames = ["bin", "obj", "node_modules", ".git", ".vs"];

    private static readonly IReadOnlyList<IDetector> Detectors =
    [
        // EF-model-aware: need the DbContext/entity/navigation model to say anything
        new NPlusOneDetector(),
        new MissingAsNoTrackingDetector(),
        new MissingIncludeDetector(),
        new UnusedNavigationPropertyDetector(),
        new MultipleSaveChangesDetector(),

        // Query shape: reason about the fluent LINQ chain hanging off a DbSet
        new ClientSideEvaluationDetector(),
        new QueryInLoopDetector(),
        new SaveChangesInLoopDetector(),
        new UnboundedQueryDetector(),
        new CartesianIncludeDetector(),
        new InefficientCountDetector(),

        // General .NET throughput: apply to any C# codebase, EF Core or not
        new SyncOverAsyncDetector(),
        new AsyncVoidDetector(),
        new StringConcatInLoopDetector(),
        new BlockingCallInAsyncMethodDetector(),

        // Correctness and security: bugs and vulnerabilities, not just cost
        new RawSqlInjectionRiskDetector(),
        new StringBasedIncludeDetector(),
        new MissingCancellationTokenDetector(),
        new DbContextSingletonLifetimeDetector()
    ];

    public AnalysisReport Analyze(string targetDirectory, int maxFiles = int.MaxValue)
    {
        if (!Directory.Exists(targetDirectory))
            throw new DirectoryNotFoundException($"Target directory not found: {targetDirectory}");

        var startedAt = DateTimeOffset.UtcNow;

        var csFiles = EnumerateCSharpFiles(targetDirectory).ToList();
        if (csFiles.Count > maxFiles)
        {
            throw new InvalidOperationException(
                $"Target contains {csFiles.Count} .cs files, exceeding the configured limit of {maxFiles}.");
        }

        var sourceByPath = csFiles.ToDictionary(path => path, File.ReadAllText);
        var trees = sourceByPath
            .Select(kv => CSharpSyntaxTree.ParseText(kv.Value, path: kv.Key))
            .ToList();

        var model = EfModelBuilder.Build(trees);

        var suppressionsByFile = sourceByPath.ToDictionary(
            kv => kv.Key, kv => SuppressionScanner.ScanFile(kv.Value));

        var rawFindings = Detectors
            .SelectMany(d => d.Detect(trees, model))
            .OrderBy(f => f.FilePath)
            .ThenBy(f => f.Line)
            .ToList();

        var findings = rawFindings
            .Where(f => !(suppressionsByFile.TryGetValue(f.FilePath, out var fileMap) &&
                          SuppressionScanner.IsSuppressed(fileMap, f)))
            .ToList();

        return new AnalysisReport
        {
            TargetPath = targetDirectory,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            FilesScanned = csFiles.Count,
            DbContextsFound = model.DbContextDbSets.Count,
            EntityTypesFound = model.EntityTypes.Count,
            Findings = findings,
            SuppressedCount = rawFindings.Count - findings.Count
        };
    }

    private static IEnumerable<string> EnumerateCSharpFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subDirs;
            IEnumerable<string> files;

            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir, "*.cs");
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            foreach (var subDir in subDirs)
            {
                if (!ExcludedDirectoryNames.Contains(Path.GetFileName(subDir)))
                    stack.Push(subDir);
            }
        }
    }
}
