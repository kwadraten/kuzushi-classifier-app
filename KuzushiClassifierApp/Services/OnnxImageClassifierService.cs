using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public sealed class OnnxImageClassifierService : IImageClassifierService, IDisposable
{
    private readonly InferenceSession _session;
    private readonly OnnxModelMetadata _metadata;

    public OnnxImageClassifierService(IModelPathProvider modelPathProvider)
    {
        ArgumentNullException.ThrowIfNull(modelPathProvider);

        var metadataPath = Path.ChangeExtension(modelPathProvider.ClassifierModelPath, ".metadata.json");
        _metadata = OnnxModelMetadata.Load(metadataPath);
        _session = new InferenceSession(modelPathProvider.ClassifierModelPath);
    }

    public Task<KuzushiPrediction> PredictAsync(
        KuzushiImage image,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        cancellationToken.ThrowIfCancellationRequested();

        var inputTensor = OnnxImageTensorFactory.CreateInputTensor(image, _metadata.ImageSize);
        var logits = Run(inputTensor);
        var probabilities = Softmax(logits);

        var candidates = probabilities
            .Select((confidence, index) => new PredictionCandidate(
                DecodeLabel(index),
                confidence))
            .OrderByDescending(candidate => candidate.Confidence)
            .Take(topK)
            .ToArray();

        return Task.FromResult(new KuzushiPrediction(candidates));
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

    private string DecodeLabel(int index)
    {
        if (index < 0 || index >= _metadata.Labels.Length)
        {
            return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return UnicodeLabelDecoder.Decode(_metadata.Labels[index]);
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
