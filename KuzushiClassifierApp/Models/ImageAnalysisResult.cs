namespace KuzushiClassifierApp.Models;

public sealed record ImageAnalysisResult(
    KuzushiPrediction Prediction,
    IReadOnlyList<SimilarImageResult> SimilarImages);
