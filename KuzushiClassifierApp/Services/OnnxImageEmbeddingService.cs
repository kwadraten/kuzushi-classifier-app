using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public sealed class OnnxImageEmbeddingService : IImageEmbeddingService, IDisposable
{
    private readonly InferenceSession _session;
    private readonly OnnxModelMetadata _metadata;

    public OnnxImageEmbeddingService(IModelPathProvider modelPathProvider)
    {
        ArgumentNullException.ThrowIfNull(modelPathProvider);

        var metadataPath = Path.ChangeExtension(modelPathProvider.EmbeddingModelPath, ".metadata.json");
        _metadata = OnnxModelMetadata.Load(metadataPath);
        _session = new InferenceSession(modelPathProvider.EmbeddingModelPath);
    }

    public Task<ImageEmbedding> EmbedAsync(
        KuzushiImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        var inputTensor = OnnxImageTensorFactory.CreateInputTensor(image, _metadata.ImageSize);
        var embedding = Run(inputTensor);

        return Task.FromResult(new ImageEmbedding(Normalize(embedding)));
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private float[] Run(DenseTensor<float> inputTensor)
    {
        var input = NamedOnnxValue.CreateFromTensor(_metadata.InputName, inputTensor);
        using var results = _session.Run(new[] { input });

        var output = results.FirstOrDefault(result => result.Name == _metadata.OutputName)
            ?? results.First();

        return output.AsEnumerable<float>().ToArray();
    }

    private static float[] Normalize(float[] vector)
    {
        double magnitudeSquared = 0;

        foreach (var value in vector)
        {
            magnitudeSquared += value * value;
        }

        if (magnitudeSquared == 0)
        {
            return vector;
        }

        var magnitude = Math.Sqrt(magnitudeSquared);

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] / magnitude);
        }

        return vector;
    }
}
