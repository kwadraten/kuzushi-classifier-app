using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public sealed class OnnxImageClassifierService : IImageClassifierService, IDisposable
{
    private readonly string _modelPath;
    private readonly string _metadataPath;
    private InferenceSession? _session;
    private OnnxModelMetadata? _metadata;

    public OnnxImageClassifierService(IModelPathProvider modelPathProvider)
    {
        ArgumentNullException.ThrowIfNull(modelPathProvider);

        _modelPath = modelPathProvider.ClassifierModelPath;
        _metadataPath = Path.ChangeExtension(_modelPath, ".metadata.json");
    }

    public Task<KuzushiPrediction> PredictAsync(
        KuzushiImage image,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        cancellationToken.ThrowIfCancellationRequested();

        var (session, metadata) = EnsureLoaded();
        var inputTensor = OnnxImageTensorFactory.CreateInputTensor(image, metadata.ImageSize);
        var logits = Run(session, metadata, inputTensor);
        var probabilities = Softmax(logits);

        var candidates = probabilities
            .Select((confidence, index) => new PredictionCandidate(
                DecodeLabel(metadata, index),
                confidence))
            .OrderByDescending(candidate => candidate.Confidence)
            .Take(topK)
            .ToArray();

        return Task.FromResult(new KuzushiPrediction(candidates));
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

    private static string DecodeLabel(OnnxModelMetadata metadata, int index)
    {
        if (index < 0 || index >= metadata.Labels.Length)
        {
            return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return UnicodeLabelDecoder.Decode(metadata.Labels[index]);
    }

    private static float[] Softmax(IReadOnlyList<float> logits)
    {
        var max = logits.Max();
        var exps = new double[logits.Count];
        double sum = 0;

        for (var index = 0; index < logits.Count; index++)
        {
            var exp = Math.Exp(logits[index] - max);
            exps[index] = exp;
            sum += exp;
        }

        var probabilities = new float[logits.Count];

        for (var index = 0; index < logits.Count; index++)
        {
            probabilities[index] = (float)(exps[index] / sum);
        }

        return probabilities;
    }
}
