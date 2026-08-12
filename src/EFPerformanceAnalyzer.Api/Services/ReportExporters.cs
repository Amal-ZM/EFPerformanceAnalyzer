using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EFPerformanceAnalyzer.Api.Contracts;

namespace EFPerformanceAnalyzer.Api.Services;

/// <summary>
/// Renders a completed run into formats other tools already understand, so findings don't have
/// to live only inside this app: SARIF for GitHub Code Scanning / VS Code's Problems panel, CSV
/// for a spreadsheet, Markdown for pasting into a ticket or PR description.
/// </summary>
public static class ReportExporters
{
    private static readonly JsonSerializerOptions SarifJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // SARIF 2.1.0 is a strict schema requiring camelCase property names — GitHub Code Scanning
        // and VS Code's Problems panel will silently fail to parse a PascalCase-serialized file.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ToSarif(AnalysisRunDetailResponse detail)
    {
        var ruleIds = detail.Findings.Select(f => f.Category).Distinct().OrderBy(c => c).ToList();

        var sarif = new SarifLog
        {
            Runs =
            [
                new SarifRun
                {
                    Tool = new SarifTool
                    {
                        Driver = new SarifDriver
                        {
                            Name = "EFPerformanceAnalyzer",
                            InformationUri = "https://github.com",
                            Version = "1.0.0",
                            Rules = ruleIds.Select(id => new SarifRule
                            {
                                Id = id,
                                ShortDescription = new SarifText { Text = HumanizeCategory(id) }
                            }).ToList()
                        }
                    },
                    Results = detail.Findings.Select(f => new SarifResult
                    {
                        RuleId = f.Category,
                        Level = f.Severity switch { "Critical" => "error", "Warning" => "warning", _ => "note" },
                        Message = new SarifText { Text = f.Message },
                        Locations =
                        [
                            new SarifLocation
                            {
                                PhysicalLocation = new SarifPhysicalLocation
                                {
                                    ArtifactLocation = new SarifArtifactLocation { Uri = f.FilePath.Replace('\\', '/') },
                                    Region = new SarifRegion { StartLine = Math.Max(1, f.Line) }
                                }
                            }
                        ]
                    }).ToList()
                }
            ]
        };

        return JsonSerializer.Serialize(sarif, SarifJsonOptions);
    }

    public static string ToCsv(AnalysisRunDetailResponse detail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Severity,Category,File,Line,Member,Message,Recommendation");
        foreach (var f in detail.Findings)
        {
            sb.Append(CsvField(f.Severity)).Append(',')
              .Append(CsvField(f.Category)).Append(',')
              .Append(CsvField(f.FilePath)).Append(',')
              .Append(f.Line).Append(',')
              .Append(CsvField(f.MemberName)).Append(',')
              .Append(CsvField(f.Message)).Append(',')
              .Append(CsvField(f.Recommendation ?? "")).Append('\n');
        }
        return sb.ToString();
    }

    public static string ToMarkdown(AnalysisRunDetailResponse detail)
    {
        var s = detail.Summary;
        var sb = new StringBuilder();
        sb.AppendLine($"# EF Performance Analyzer — Run #{s.RunId}");
        sb.AppendLine();
        sb.AppendLine($"- **Target:** `{s.TargetPath}`");
        sb.AppendLine($"- **Scanned:** {s.StartedAtUtc:u}");
        sb.AppendLine($"- **Files scanned:** {s.FilesScanned}  ·  **DbContexts:** {s.DbContextsFound}  ·  **Entity types:** {s.EntityTypesFound}");
        sb.AppendLine($"- **Total findings:** {s.TotalFindings}" + (s.SuppressedCount > 0 ? $"  ·  **Suppressed:** {s.SuppressedCount}" : ""));
        sb.AppendLine();

        foreach (var group in detail.Findings.GroupBy(f => f.Severity).OrderBy(g => g.Key switch { "Critical" => 0, "Warning" => 1, _ => 2 }))
        {
            sb.AppendLine($"## {group.Key} ({group.Count()})");
            sb.AppendLine();
            foreach (var f in group.OrderBy(f => f.FilePath).ThenBy(f => f.Line))
            {
                sb.AppendLine($"### `{f.FilePath}:{f.Line}` — {HumanizeCategory(f.Category)}");
                sb.AppendLine();
                sb.AppendLine($"**Member:** `{f.MemberName}`");
                sb.AppendLine();
                sb.AppendLine(f.Message);
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(f.CodeSnippet);
                sb.AppendLine("```");
                if (!string.IsNullOrEmpty(f.Recommendation))
                {
                    sb.AppendLine();
                    sb.AppendLine($"**Fix:** {f.Recommendation}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string HumanizeCategory(string category) =>
        System.Text.RegularExpressions.Regex.Replace(category, "(?<!^)([A-Z])", " $1");

    // ---- minimal SARIF 2.1.0 object model (only what we emit) ----
    private sealed class SarifLog
    {
        [JsonPropertyName("$schema")]
        public string Schema { get; init; } = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json";
        public string Version { get; init; } = "2.1.0";
        public required List<SarifRun> Runs { get; init; }
    }

    private sealed class SarifRun
    {
        public required SarifTool Tool { get; init; }
        public required List<SarifResult> Results { get; init; }
    }

    private sealed class SarifTool { public required SarifDriver Driver { get; init; } }

    private sealed class SarifDriver
    {
        public required string Name { get; init; }
        public required string InformationUri { get; init; }
        public required string Version { get; init; }
        public required List<SarifRule> Rules { get; init; }
    }

    private sealed class SarifRule
    {
        public required string Id { get; init; }
        public required SarifText ShortDescription { get; init; }
    }

    private sealed class SarifResult
    {
        public required string RuleId { get; init; }
        public required string Level { get; init; }
        public required SarifText Message { get; init; }
        public required List<SarifLocation> Locations { get; init; }
    }

    private sealed class SarifText { public required string Text { get; init; } }
    private sealed class SarifLocation { public required SarifPhysicalLocation PhysicalLocation { get; init; } }
    private sealed class SarifPhysicalLocation
    {
        public required SarifArtifactLocation ArtifactLocation { get; init; }
        public required SarifRegion Region { get; init; }
    }
    private sealed class SarifArtifactLocation { public required string Uri { get; init; } }
    private sealed class SarifRegion { public required int StartLine { get; init; } }
}
