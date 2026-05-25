namespace KuzushiClassifierApp.Models;

public sealed record PredictionCandidate(
    string Label,
    float Confidence);
