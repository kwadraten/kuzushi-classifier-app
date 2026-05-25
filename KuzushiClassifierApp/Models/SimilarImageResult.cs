namespace KuzushiClassifierApp.Models;

public sealed record SimilarImageResult(
    DatasetImage Image,
    float Similarity);
