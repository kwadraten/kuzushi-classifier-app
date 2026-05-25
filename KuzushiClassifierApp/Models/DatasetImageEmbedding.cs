namespace KuzushiClassifierApp.Models;

public sealed record DatasetImageEmbedding(
    DatasetImage Image,
    IReadOnlyList<float> Vector);
