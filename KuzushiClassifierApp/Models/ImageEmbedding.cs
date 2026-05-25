namespace KuzushiClassifierApp.Models;

public sealed record ImageEmbedding(IReadOnlyList<float> Vector)
{
    public int Dimensions => Vector.Count;
}
