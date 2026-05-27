using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public sealed class OnnxImageEmbeddingService : IImageEmbeddingService, IDisposable
{
    private readonly string _modelPath;
    private readonly string _metadataPath;
    private InferenceSession? _session;
    private OnnxModelMetadata? _metadata;

    public OnnxImageEmbeddingService(IModelPathProvider modelPathProvider)
    {
        ArgumentNullException.ThrowIfNull(modelPathProvider);

        _modelPath = modelPathProvider.EmbeddingModelPath;
        _metadataPath = Path.ChangeExtension(_modelPath, ".metadata.json");
    }

    public Task<ImageEmbedding> EmbedAsync(
        KuzushiImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        var (session, metadata) = EnsureLoaded();
        var inputTensor = OnnxImageTensorFactory.CreateInputTensor(image, metadata.ImageSize);
        var embedding = Run(session, metadata, inputTensor);

        return Task.FromResult(new ImageEmbedding(Normalize(embedding)));
    }

    public void Dispose()
    {
        _session?.Dispose();
    }

    private (InferenceSession Session, OnnxModelMetadata Metadata) EnsureLoaded()
    {
        if (_session is not null && _metadata is not null)
        {
            return (_session, _metadata);
        }

        _metadata = OnnxModelMetadata.Load(_metadataPath);
        _session = new InferenceSession(_modelPath);

        return (_session, _metadata);
    }

    private static float[] Run(
        InferenceSession session,
        OnnxModelMetadata metadata,
        DenseTensor<float> inputTensor)
    {
        var input = NamedOnnxValue.CreateFromTensor(metadata.InputName, inputTensor);
        using var results = session.Run(new[] { input });

        var output = results.FirstOrDefault(result => result.Name == metadata.OutputName)
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
