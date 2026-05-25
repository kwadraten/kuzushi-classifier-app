namespace KuzushiClassifierApp.Models;

public sealed record KuzushiPrediction(IReadOnlyList<PredictionCandidate> Candidates)
{
    public static KuzushiPrediction Empty { get; } = new(Array.Empty<PredictionCandidate>());

    public KuzushiPrediction Top(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        return new KuzushiPrediction(
            Candidates
                .OrderByDescending(candidate => candidate.Confidence)
                .Take(count)
                .ToArray());
    }
}
