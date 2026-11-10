using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;
using KuzushiClassifierApp.Plugin;
using KuzushiClassifierApp.Services;
using SkiaSharp;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// Per-test isolated data root under the repository .tmp directory.
/// Nothing here is a real model/dataset: files are only touched so the
/// filesystem readiness probe reports the requested shape.
/// </summary>
internal sealed class FakeDataRoot : IAppDataPathProvider, IDisposable
{
    private FakeDataRoot(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string ModelsDirectory => Path.Combine(Root, "models");

    public string DatasetsDirectory => Path.Combine(Root, "datasets");

    public string GetAppDataDirectory() => Root;

    public string GetModelCacheDirectory() => ModelsDirectory;

    public string GetDatasetCacheDirectory() => DatasetsDirectory;

    public static FakeDataRoot Create()
    {
        var root = Path.Combine(RepoRoot(), ".tmp", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new FakeDataRoot(root);
    }

    public void MakeClassifierReady()
    {
        var paths = new CachedModelPathProvider(this);
        Touch(paths.ClassifierModelPath);
        Touch(CachedModelPathProvider.MetadataPathFor(paths.ClassifierModelPath));
    }

    public void MakeEmbeddingReady()
    {
        var paths = new CachedModelPathProvider(this);
        Touch(paths.EmbeddingModelPath);
        Touch(CachedModelPathProvider.MetadataPathFor(paths.EmbeddingModelPath));
    }

    public void MakeDatasetReady()
    {
        Directory.CreateDirectory(DatasetsDirectory);
        File.WriteAllBytes(
            Path.Combine(DatasetsDirectory, ParquetFileEmbeddingCacheService.CacheFileName),
            new byte[2048]);
    }

    public PluginAssetProbe CreateProbe(PluginManagedFiles? managedFiles = null) =>
        new(new CachedModelPathProvider(this, managedFiles));

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1 });
    }

    internal static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "patchouli-plugin.toml")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A fake host managed-file store: a directory plus the matching
/// PluginManagedFiles map. The plugin only reads files the host has already
/// placed — Touch helpers simulate exactly that completed download.
/// </summary>
internal sealed class FakeManagedStore : IDisposable
{
    private FakeManagedStore(string root)
    {
        Root = root;
        Files = new PluginManagedFiles(root, null);
    }

    public string Root { get; }

    public PluginManagedFiles Files { get; }

    public string AbsolutePathFor(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static FakeManagedStore Create()
    {
        var root = Path.Combine(
            FakeDataRoot.RepoRoot(), ".tmp", "tests", "managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new FakeManagedStore(root);
    }

    public void Touch(string relativePath, int bytes = 1)
    {
        var absolute = AbsolutePathFor(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, new byte[bytes]);
    }

    public void TouchAllDeclared()
    {
        Touch(PluginManagedFiles.ClassifierModel);
        Touch(PluginManagedFiles.ClassifierMetadata);
        Touch(PluginManagedFiles.EmbeddingModel);
        Touch(PluginManagedFiles.EmbeddingMetadata);
        Touch(PluginManagedFiles.DatasetParquet, bytes: 2048);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeClassifierService : IImageClassifierService
{
    public Func<KuzushiImage, int, CancellationToken, Task<KuzushiPrediction>>? Handler;

    public int CallCount;

    public Task<KuzushiPrediction> PredictAsync(
        KuzushiImage image,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref CallCount);
        return Handler?.Invoke(image, topK, cancellationToken)
            ?? Task.FromResult(new KuzushiPrediction(new PredictionCandidate[]
            {
                new("あ", 0.91f),
                new("い", 0.04f),
            }));
    }
}

internal sealed class FakeEmbeddingService : IImageEmbeddingService
{
    public Task<ImageEmbedding> EmbedAsync(
        KuzushiImage image,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ImageEmbedding(new float[] { 1f, 0f, 0f }));
    }
}

internal sealed class FakeEmbeddingIndexService : IEmbeddingIndexService
{
    public bool IsReady { get; private set; }

    public int Count { get; private set; }

    public bool BuildCalled { get; private set; }

    public Task BuildAsync(
        IEnumerable<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        BuildCalled = true;
        IsReady = true;
        Count = 2;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SimilarImageResult>> SearchAsync(
        ImageEmbedding query,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SimilarImageResult>>(new SimilarImageResult[]
        {
            new(new DatasetImage("img-1", "あ"), 0.98f),
            new(new DatasetImage("img-2", "い"), 0.87f),
        });
    }
}

internal static class TestImages
{
    /// <summary>A small valid PNG generated in memory.</summary>
    public static string PngBase64(int width = 64, int height = 64)
        => EncodeBase64(SKEncodedImageFormat.Png, width, height);

    public static string EncodeBase64(SKEncodedImageFormat format, int width = 64, int height = 64)
    {
        using var image = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image.SetPixel(x, y, new SKColor((byte)x, (byte)y, 128));
            }
        }

        using var encoded = image.Encode(format, 100);
        return Convert.ToBase64String(encoded.ToArray());
    }

    /// <summary>
    /// A minimal PNG header advertising huge dimensions — valid signature +
    /// IHDR, tiny payload, so header inspection must reject it before decode.
    /// </summary>
    public static string OversizedPngBase64(int width = 9000, int height = 9000)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        void Chunk(string type, byte[] data)
        {
            bytes.AddRange(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(data.Length)));
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            bytes.AddRange(data);
            var crc = new byte[4];
            bytes.AddRange(crc); // CRC is not validated on header inspection
        }

        var ihdr = new byte[13];
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(width)).CopyTo(ihdr, 0);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(height)).CopyTo(ihdr, 4);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // truecolour
        Chunk("IHDR", ihdr);
        Chunk("IEND", Array.Empty<byte>());

        return Convert.ToBase64String(bytes.ToArray());
    }
}
