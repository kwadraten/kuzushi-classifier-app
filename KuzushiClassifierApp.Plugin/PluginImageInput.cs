using KuzushiClassifierApp.Models;
using SkiaSharp;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Validates send-args image input before it ever reaches ONNX:
/// base64 shape, the 8 MiB encoded-payload cap, and decompression bounds read
/// from image headers only (max 8192 px per edge, max 16,777,216 pixels).
/// </summary>
public static class PluginImageInput
{
    public const int MaxDecodedBytes = 8 * 1024 * 1024;
    public const int MaxEdgePixels = 8192;
    public const int MaxTotalPixels = 16_777_216;

    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.Ordinal)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
    };

    public static KuzushiImage Parse(
        string? imageBase64,
        string? declaredMediaType)
    {
        if (declaredMediaType is not null && !AllowedMediaTypes.Contains(declaredMediaType))
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                $"args.media_type must be one of image/png, image/jpeg, image/webp; got '{declaredMediaType}'.");
        }

        if (string.IsNullOrEmpty(imageBase64))
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                "args.image_base64 is required and must be a non-empty base64 string.");
        }

        // base64 expands to 4/3 of the decoded size; reject oversize payloads
        // before paying for the decode.
        if (imageBase64.Length > ((long)MaxDecodedBytes + 2) / 3 * 4 + 8)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                $"args.image_base64 decodes to more than {MaxDecodedBytes} bytes (8 MiB cap).");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(imageBase64);
        }
        catch (FormatException ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                "args.image_base64 is not valid base64.",
                ex);
        }

        if (bytes.Length == 0)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                "args.image_base64 decoded to zero bytes.");
        }

        if (bytes.Length > MaxDecodedBytes)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                $"Decoded image is {bytes.Length} bytes; the limit is {MaxDecodedBytes} (8 MiB).");
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        var detectedMediaType = codec?.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => null,
        };
        if (detectedMediaType is null)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                "Decoded bytes are not a recognized image (png, jpeg or webp).");
        }

        var width = codec!.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0
            || width > MaxEdgePixels
            || height > MaxEdgePixels
            || (long)width * height > MaxTotalPixels)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                $"Image dimensions {width}x{height} exceed the allowed bounds " +
                $"(max {MaxEdgePixels} px per edge, max {MaxTotalPixels} total pixels).");
        }

        if (declaredMediaType is not null
            && !string.Equals(declaredMediaType, detectedMediaType, StringComparison.Ordinal))
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                $"args.media_type '{declaredMediaType}' does not match the detected format '{detectedMediaType}'.");
        }

        return KuzushiImage.FromBytes(
            bytes,
            fileName: "input",
            mediaType: detectedMediaType);
    }
}
