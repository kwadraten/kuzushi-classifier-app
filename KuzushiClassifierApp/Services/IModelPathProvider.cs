namespace KuzushiClassifierApp.Services;

public interface IModelPathProvider
{
    string ClassifierModelPath { get; }

    string EmbeddingModelPath { get; }
}
