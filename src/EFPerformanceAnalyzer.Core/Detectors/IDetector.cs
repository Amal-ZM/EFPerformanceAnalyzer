using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;

namespace EFPerformanceAnalyzer.Core.Detectors;

public interface IDetector
{
    IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model);
}
