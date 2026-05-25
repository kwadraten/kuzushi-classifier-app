using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public static class OnnxImageTensorFactory
{
    public static DenseTensor<float> CreateInputTensor(
        KuzushiImage image,
        int imageSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageSize);

        using var decoded = Image.Load<Rgba32>(image.Bytes);
        decoded.Mutate(context => context.Resize(imageSize, imageSize));

        var tensor = new DenseTensor<float>(new[] { 1, 3, imageSize, imageSize });

        decoded.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);

                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = pixel.R / 255f;
                    tensor[0, 1, y, x] = pixel.G / 255f;
                    tensor[0, 2, y, x] = pixel.B / 255f;
                }
            }
        });

        return tensor;
    }
}
