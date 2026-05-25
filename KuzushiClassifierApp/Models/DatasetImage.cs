namespace KuzushiClassifierApp.Models;

public sealed record DatasetImage(
    string Id,
    string Label,
    string? SourceUri = null,
    string? LocalPath = null);
