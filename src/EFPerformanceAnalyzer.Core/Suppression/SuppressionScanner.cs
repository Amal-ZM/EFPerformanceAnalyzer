using System.Text.RegularExpressions;
using EFPerformanceAnalyzer.Core.Models;

namespace EFPerformanceAnalyzer.Core.Suppression;

/// <summary>
/// A finding can be silenced with a <c>// ef-analyzer-ignore</c> comment (all categories) or
/// <c>// ef-analyzer-ignore: MissingAsNoTracking, NPlusOneQuery</c> (specific ones), placed either
/// trailing the flagged line or alone on the line directly above it — matching the convention most
/// linters already use, so it needs no separate config file to maintain.
/// </summary>
public static partial class SuppressionScanner
{
    [GeneratedRegex(@"//\s*ef-analyzer-ignore\s*(?::\s*(?<categories>[A-Za-z0-9_,\s]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex SuppressionPattern();

    /// <summary>Line number (1-based) -> null (suppress everything on that line) or the specific category names.</summary>
    public static Dictionary<int, HashSet<string>?> ScanFile(string sourceText)
    {
        var map = new Dictionary<int, HashSet<string>?>();
        var lines = sourceText.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var match = SuppressionPattern().Match(lines[i]);
            if (!match.Success)
                continue;

            HashSet<string>? categories = null;
            var categoriesGroup = match.Groups["categories"];
            if (categoriesGroup.Success)
            {
                categories = categoriesGroup.Value
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var lineNumber = i + 1;
            Merge(map, lineNumber, categories);       // trailing comment on the flagged line itself
            Merge(map, lineNumber + 1, categories);    // standalone comment on the line above the flagged one
        }

        return map;
    }

    public static bool IsSuppressed(Dictionary<int, HashSet<string>?> fileSuppressions, Finding finding)
    {
        if (!fileSuppressions.TryGetValue(finding.Line, out var categories))
            return false;

        return categories is null || categories.Contains(finding.Category.ToString());
    }

    private static void Merge(Dictionary<int, HashSet<string>?> map, int line, HashSet<string>? categories)
    {
        if (!map.TryGetValue(line, out var existing))
        {
            map[line] = categories;
            return;
        }

        // null means "suppress everything" and stays sticky even if another suppression on the
        // same line names specific categories.
        if (existing is null || categories is null)
        {
            map[line] = null;
            return;
        }

        existing.UnionWith(categories);
    }
}
