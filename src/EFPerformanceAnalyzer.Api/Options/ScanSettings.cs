namespace EFPerformanceAnalyzer.Api.Options;

public sealed class ScanSettings
{
    public const string SectionName = "ScanSettings";

    /// <summary>
    /// Absolute directory prefixes the analyzer is allowed to read from. A scan request whose
    /// resolved target path does not fall under one of these is rejected. Empty = no scans allowed
    /// (fail closed) until an operator explicitly configures this.
    /// </summary>
    public List<string> AllowedRoots { get; set; } = [];

    public int MaxFilesPerScan { get; set; } = 100_000;

    public int ScanTimeoutSeconds { get; set; } = 1800;

    /// <summary>Max accepted size, in bytes, of an uploaded .zip (compressed). Default 1 GB.</summary>
    public long MaxUploadSizeBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Decompression-bomb guard: extraction aborts once total uncompressed bytes written exceeds
    /// this multiple of MaxUploadSizeBytes.
    /// </summary>
    public int MaxUploadExpansionRatio { get; set; } = 5;
}
