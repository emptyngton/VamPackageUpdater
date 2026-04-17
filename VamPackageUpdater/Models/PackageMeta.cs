namespace VamPackageUpdater.Models;

public sealed record PackageMeta
{
    public string? CreatorName { get; init; }
    public string? PackageName { get; init; }
    public string? LicenseType { get; init; }
    public string? Description { get; init; }
    public string? Credits { get; init; }
    public string? ProgramVersion { get; init; }
    public long FileSize { get; init; }
    public int FileCount { get; init; }
}
