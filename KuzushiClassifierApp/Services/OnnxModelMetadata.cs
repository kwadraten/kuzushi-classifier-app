using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuzushiClassifierApp.Services;

public sealed record OnnxModelMetadata(
    [property: JsonPropertyName("image_size")] int ImageSize,
    [property: JsonPropertyName("input_name")] string InputName,
    [property: JsonPropertyName("output_name")] string OutputName,
    [property: JsonPropertyName("labels")] string[] Labels)
{
    public static OnnxModelMetadata Load(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException("ONNX model metadata file was not found.", metadataPath);
        }

        var metadata = JsonSerializer.Deserialize<OnnxModelMetadata>(
            File.ReadAllText(metadataPath),
            JsonSerializerOptions.Web);

        return metadata ?? throw new InvalidOperationException(
            $"Could not parse ONNX model metadata: {metadataPath}");
    }
}
