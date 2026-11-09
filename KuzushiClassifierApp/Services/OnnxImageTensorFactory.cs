using Microsoft.ML.OnnxRuntime.Tensors;
using KuzushiClassifierApp.Models;
using SkiaSharp;

namespace KuzushiClassifierApp.Services;

public static class OnnxImageTensorFactory
{
    public static DenseTensor<float> CreateInputTensor(
        KuzushiImage image,
        int imageSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageSize);

        using var data = SKData.CreateCopy(image.Bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Image could not be decoded.");
        using var decoded = new SKBitmap(new SKImageInfo(
            codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            throw new InvalidDataException("Image could not be decoded.");
        }
        using var resized = new SKBitmap(new SKImageInfo(imageSize, imageSize, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!decoded.ScalePixels(resized, new SKSamplingOptions(SKCubicResampler.Mitchell)))
        {
            throw new InvalidDataException("Image could not be resized.");
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, imageSize, imageSize });
        for (var y = 0; y < imageSize; y++)
        {
            for (var x = 0; x < imageSize; x++)
            {
                var pixel = resized.GetPixel(x, y);
                tensor[0, 0, y, x] = pixel.Red / 255f;
                tensor[0, 1, y, x] = pixel.Green / 255f;
                tensor[0, 2, y, x] = pixel.Blue / 255f;
            }
        }

        return tensor;
    }
}
