using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotVector.Api;
using DotVector.Index.Hnsw;
using DotVector.Model;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Parquet;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

var options = PrebuildOptions.Parse(args);
await new PrebuildPipeline(options).RunAsync();

internal sealed class PrebuildPipeline(PrebuildOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient = CreateHttpClient();
    private readonly BlockingCollection<string> _readyShardQueue = new(options.DownloadParallelism * 2);
    private readonly BlockingCollection<BuiltRecord> _builtRecordQueue = new(options.BuildWorkers * options.GroupSize);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public async Task RunAsync()
    {
        Console.WriteLine($"repo root: {options.RepoRoot}");
        Console.WriteLine($"model dir: {options.ModelDirectory}");
        Console.WriteLine($"dataset shard dir: {options.DatasetShardDirectory}");
        Console.WriteLine($"output root: {options.OutputRoot}");
        Console.WriteLine($"download parallelism: {options.DownloadParallelism}");
        Console.WriteLine($"build workers: {options.BuildWorkers}");
        Console.WriteLine($"webp quality: {options.WebpQuality}");
        Console.WriteLine($"max width: {options.MaxWidth}");

        PrepareOutputDirectories();
        await EnsureModelFilesAsync();

        var metadata = LoadEmbeddingMetadata(options.EmbeddingMetadataPath);
        var writerTask = Task.Run(() => WriteDotVectorIndex(metadata));
        var buildTasks = Enumerable
            .Range(0, options.BuildWorkers)
            .Select(workerId => Task.Run(() => ProcessReadyShards(workerId + 1, metadata)))
            .ToArray();

        var shardPaths = await ListDatasetShardsAsync();
        await DownloadShardsAndEnqueueAsync(shardPaths);

        _readyShardQueue.CompleteAdding();
        await Task.WhenAll(buildTasks);

        _builtRecordQueue.CompleteAdding();
        await writerTask;

        CreateTarPackage();
        Console.WriteLine($"complete in {_clock.Elapsed.TotalMinutes:F1} min");
    }

    private void PrepareOutputDirectories()
    {
        if (options.Force && Directory.Exists(options.OutputRoot))
        {
            Directory.Delete(options.OutputRoot, recursive: true);
        }

        Directory.CreateDirectory(options.OutputRoot);
        Directory.CreateDirectory(options.ImageDirectory);
        Directory.CreateDirectory(options.MetadataDirectory);

        if (options.Force && Directory.Exists(options.VectorDirectory))
        {
            Directory.Delete(options.VectorDirectory, recursive: true);
        }

        Directory.CreateDirectory(options.VectorDirectory);

        if (options.Force && File.Exists(options.RecordsPath))
        {
            File.Delete(options.RecordsPath);
        }
    }

    private async Task EnsureModelFilesAsync()
    {
        Directory.CreateDirectory(options.ModelDirectory);

        var downloads = PrebuildOptions.ModelFiles
            .Select(fileName => new DownloadItem(
                $"https://huggingface.co/{options.ModelRepo}/resolve/main/{fileName}",
                Path.Combine(options.ModelDirectory, fileName),
                fileName))
            .ToArray();

        await RunBoundedAsync(downloads, options.DownloadParallelism, DownloadIfMissingAsync);
    }

    private async Task<string[]> ListDatasetShardsAsync()
    {
        var apiUrl = $"https://huggingface.co/api/datasets/{options.DatasetRepo}";

        try
        {
            var json = await _httpClient.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("siblings", out var siblings))
            {
                var files = siblings
                    .EnumerateArray()
                    .Select(sibling => sibling.TryGetProperty("rfilename", out var rfilename)
                        ? rfilename.GetString()
                        : null)
                    .Where(path => path is not null
                        && path.StartsWith("data/", StringComparison.Ordinal)
                        && path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                    .Cast<string>()
                    .Order()
                    .ToArray();

                if (files.Length > 0)
                {
                    return files;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not list dataset shards from HF API, using fallback names: {ex.Message}");
        }

        return
        [
            "data/train-00000-of-00005.parquet",
            "data/train-00001-of-00005.parquet",
            "data/train-00002-of-00005.parquet",
            "data/train-00003-of-00005.parquet",
            "data/train-00004-of-00005.parquet",
        ];
    }

    private async Task DownloadShardsAndEnqueueAsync(string[] shardPaths)
    {
        Directory.CreateDirectory(options.DatasetShardDirectory);

        var downloads = shardPaths
            .Select(path => new DownloadItem(
                $"https://huggingface.co/datasets/{options.DatasetRepo}/resolve/main/{path}",
                Path.Combine(options.DatasetShardDirectory, Path.GetFileName(path)),
                path))
            .ToArray();

        await RunBoundedAsync(downloads, options.DownloadParallelism, async item =>
        {
            await DownloadIfMissingAsync(item);
            _readyShardQueue.Add(item.LocalPath);
            Console.WriteLine($"queued shard for processing: {Path.GetFileName(item.LocalPath)}");
        });
    }

    private async Task DownloadIfMissingAsync(DownloadItem item)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);

        if (FileReady(item.LocalPath))
        {
            Console.WriteLine($"skip existing: {item.LocalPath}");
            return;
        }

        var tempPath = item.LocalPath + ".download";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        Console.WriteLine($"download: {item.Label}");
        using var response = await _httpClient.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];
        var downloaded = 0L;
        var lastPrinted = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;

            if (downloaded - lastPrinted >= 64L * 1024L * 1024L)
            {
                lastPrinted = downloaded;
                Console.WriteLine(total is > 0
                    ? $"  {FormatBytes(downloaded)} / {FormatBytes(total.Value)} {item.Label}"
                    : $"  {FormatBytes(downloaded)} {item.Label}");
            }
        }

        await target.FlushAsync();
        File.Move(tempPath, item.LocalPath, overwrite: true);
        Console.WriteLine($"done: {item.LocalPath} ({FormatBytes(downloaded)})");
    }

    private void ProcessReadyShards(int workerId, EmbeddingMetadata metadata)
    {
        using var session = new InferenceSession(options.EmbeddingModelPath);

        foreach (var shardPath in _readyShardQueue.GetConsumingEnumerable())
        {
            Console.WriteLine($"worker {workerId} processing shard: {Path.GetFileName(shardPath)}");
            ProcessShard(workerId, session, metadata, shardPath);
        }
    }

    private void ProcessShard(int workerId, InferenceSession session, EmbeddingMetadata metadata, string shardPath)
    {
        using var fs = File.OpenRead(shardPath);
        var reader = ParquetReader.CreateAsync(fs).GetAwaiter().GetResult();
        try
        {
            var imageField = FindField(reader, "image", "bytes");
            var labelField = FindField(reader, "char");

            for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
            {
                using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
                var rowCount = (int)rowGroupReader.RowCount;
                var imageBytes = new byte[]?[rowCount];
                var labels = new string?[rowCount];

                rowGroupReader.ReadAsync(imageField, imageBytes.AsMemory()).AsTask().GetAwaiter().GetResult();
                rowGroupReader.ReadAsync(labelField, labels.AsMemory()).AsTask().GetAwaiter().GetResult();

                for (var row = 0; row < rowCount; row++)
                {
                    var bytes = imageBytes[row];
                    if (bytes is null || bytes.Length == 0)
                    {
                        continue;
                    }

                var processedCount = Interlocked.Increment(ref options.RecordCounter);
                if (processedCount > options.Take)
                {
                    return;
                }

                var id = BuildDeterministicId(shardPath, rowGroupIndex, row);
                var built = BuildRecord(session, metadata, id, labels[row] ?? "", bytes);
                _builtRecordQueue.Add(built);

                if (processedCount % 500 == 0)
                {
                    Console.WriteLine($"worker {workerId} built {processedCount:N0} records");
                }
                }
            }
        }
        finally
        {
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private BuiltRecord BuildRecord(InferenceSession session, EmbeddingMetadata metadata, string id, string label, byte[] bytes)
    {
        using var image = Image.Load<Rgba32>(bytes);
        var vector = RunEmbedding(session, metadata, image);
        var (relativePath, compressedWidth, compressedHeight) = SaveCompressedImage(id, image);

        return new BuiltRecord(
            id,
            label,
            relativePath,
            image.Width,
            image.Height,
            compressedWidth,
            compressedHeight,
            vector);
    }

    private static string BuildDeterministicId(string shardPath, int rowGroupIndex, int row)
    {
        var shardName = Path.GetFileNameWithoutExtension(shardPath)
            .Replace('-', '_')
            .Replace('.', '_');

        return $"{shardName}_rg{rowGroupIndex:D3}_row{row:D6}";
    }

    private (string RelativePath, int Width, int Height) SaveCompressedImage(string id, Image<Rgba32> image)
    {
        var rowGroupMarker = id.IndexOf("_rg", StringComparison.Ordinal);
        var bucket = rowGroupMarker > 0 ? id[..rowGroupMarker] : "unknown";
        var relativePath = Path.Combine("images-webp", bucket, id + ".webp").Replace('\\', '/');
        var absolutePath = Path.Combine(options.OutputRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        if (!options.Force && FileReady(absolutePath))
        {
            var info = Image.Identify(absolutePath);
            return (relativePath, info.Width, info.Height);
        }

        var compressedWidth = image.Width;
        var compressedHeight = image.Height;

        using var outputImage = image.Clone(context =>
        {
            if (image.Width > options.MaxWidth)
            {
                var scale = options.MaxWidth / (double)image.Width;
                compressedWidth = options.MaxWidth;
                compressedHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
                context.Resize(compressedWidth, compressedHeight);
            }
        });

        outputImage.Save(absolutePath, new WebpEncoder { Quality = options.WebpQuality });
        return (relativePath, compressedWidth, compressedHeight);
    }

    private float[] RunEmbedding(InferenceSession session, EmbeddingMetadata metadata, Image<Rgba32> image)
    {
        using var resized = image.Clone(context => context.Resize(metadata.ImageSize, metadata.ImageSize));
        var tensor = new DenseTensor<float>([1, 3, metadata.ImageSize, metadata.ImageSize]);

        resized.ProcessPixelRows(accessor =>
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

        var input = NamedOnnxValue.CreateFromTensor(metadata.InputName, tensor);
        using var results = session.Run([input]);
        var output = results.FirstOrDefault(result => result.Name == metadata.OutputName) ?? results.First();
        return Normalize(output.AsEnumerable<float>().ToArray());
    }

    private void WriteDotVectorIndex(EmbeddingMetadata metadata)
    {
        if (File.Exists(options.RecordsPath))
        {
            File.Delete(options.RecordsPath);
        }

        using var db = new VectorDatabase(options.VectorDirectory);
        var collection = db.CreateCollection<string>(
            "images",
            768,
            Metric.Cosine,
            IndexKind.Hnsw,
            HnswOptions.Default);

        using var writer = new StreamWriter(options.RecordsPath);
        var batch = new List<BuiltRecord>(options.GroupSize);
        var stored = 0;

        foreach (var record in _builtRecordQueue.GetConsumingEnumerable())
        {
            batch.Add(record);

            if (batch.Count >= options.GroupSize)
            {
                StoreBatch(collection, writer, batch);
                stored += batch.Count;
                batch.Clear();
                db.Flush();
                Console.WriteLine($"stored {stored:N0} records ({_clock.Elapsed.TotalMinutes:F1} min)");
            }
        }

        if (batch.Count > 0)
        {
            StoreBatch(collection, writer, batch);
            stored += batch.Count;
        }

        db.Flush();
        WriteManifest(metadata, stored);
        Console.WriteLine($"stored final count: {stored:N0}");
    }

    private static void StoreBatch(Collection<string> collection, StreamWriter writer, List<BuiltRecord> batch)
    {
        var vectors = batch.Select(record =>
        {
            var payload = new Dictionary<string, object>
            {
                ["label"] = record.Label,
                ["image_file"] = record.ImageFile,
                ["original_width"] = record.OriginalWidth,
                ["original_height"] = record.OriginalHeight,
                ["compressed_width"] = record.CompressedWidth,
                ["compressed_height"] = record.CompressedHeight,
            };

            return new VectorRecord<string>(record.Id, record.Vector)
            {
                Payload = payload,
            };
        }).ToArray();

        collection.InsertBatch(vectors);

        foreach (var record in batch)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                record.Id,
                record.Label,
                record.ImageFile,
                record.OriginalWidth,
                record.OriginalHeight,
                record.CompressedWidth,
                record.CompressedHeight,
            }, JsonOptions));
        }

        writer.Flush();
    }

    private void WriteManifest(EmbeddingMetadata metadata, int recordCount)
    {
        var manifest = new
        {
            SchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            Dataset = options.DatasetRepo,
            Model = options.ModelRepo,
            EmbeddingModelFile = Path.GetFileName(options.EmbeddingModelPath),
            EmbeddingDimension = 768,
            EmbeddingImageSize = metadata.ImageSize,
            VectorProvider = "DotVector.Core",
            VectorIndex = "HNSW",
            ImageFormat = "webp",
            options.WebpQuality,
            options.MaxWidth,
            RecordCount = recordCount,
            Records = "metadata/records.jsonl",
            Images = "images-webp/",
            Vectors = "vectors/dotvector-shikiji-hnsw/",
        };

        File.WriteAllText(
            options.ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            }));
    }

    private void CreateTarPackage()
    {
        if (File.Exists(options.TarPath))
        {
            File.Delete(options.TarPath);
        }

        Console.WriteLine($"creating tar: {options.TarPath}");
        TarFile.CreateFromDirectory(options.OutputRoot, options.TarPath, includeBaseDirectory: false);
    }

    private static EmbeddingMetadata LoadEmbeddingMetadata(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Embedding metadata file was not found.", path);
        }

        return JsonSerializer.Deserialize<EmbeddingMetadata>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"Could not parse embedding metadata: {path}");
    }

    private static Parquet.Schema.DataField FindField(ParquetReader reader, params string[] names)
    {
        var fields = reader.Schema.GetDataFields();
        var field = fields.FirstOrDefault(candidate =>
            names.Any(name => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

        if (field is not null)
        {
            return field;
        }

        throw new InvalidOperationException(
            $"Required field [{string.Join("/", names)}] not found. Available fields: [{string.Join(", ", fields.Select(x => x.Name))}]");
    }

    private static async Task RunBoundedAsync<T>(
        IReadOnlyCollection<T> items,
        int parallelism,
        Func<T, Task> action)
    {
        using var semaphore = new SemaphoreSlim(parallelism);

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                await action(item);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private static bool FileReady(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static float[] Normalize(float[] vector)
    {
        var magnitudeSquared = 0d;
        foreach (var value in vector)
        {
            magnitudeSquared += value * value;
        }

        if (magnitudeSquared <= 0)
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

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824L => $"{bytes / 1_073_741_824.0:F1} GiB",
            >= 1_048_576L => $"{bytes / 1_048_576.0:F1} MiB",
            >= 1024L => $"{bytes / 1024.0:F1} KiB",
            _ => $"{bytes} B",
        };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(60),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KuzushiPrebuilder", "1.0"));
        return client;
    }
}

internal sealed record DownloadItem(string Url, string LocalPath, string Label);

internal sealed record BuiltRecord(
    string Id,
    string Label,
    string ImageFile,
    int OriginalWidth,
    int OriginalHeight,
    int CompressedWidth,
    int CompressedHeight,
    float[] Vector);

internal sealed record EmbeddingMetadata(
    [property: JsonPropertyName("image_size")] int ImageSize,
    [property: JsonPropertyName("input_name")] string InputName,
    [property: JsonPropertyName("output_name")] string OutputName,
    [property: JsonPropertyName("labels")] string[] Labels);

internal sealed class PrebuildOptions
{
    public static readonly string[] ModelFiles =
    [
        "supervised_pretrain_checkpoint.onnx",
        "supervised_pretrain_checkpoint.metadata.json",
        "supervised_pretrain_checkpoint.embedding.onnx",
        "supervised_pretrain_checkpoint.embedding.metadata.json",
    ];

    public required string RepoRoot { get; init; }
    public string ModelRepo { get; init; } = "kwadraten/shikiji";
    public string DatasetRepo { get; init; } = "kwadraten/hi-utokyo-kuzushi";
    public required string ModelDirectory { get; init; }
    public required string DatasetShardDirectory { get; init; }
    public required string OutputRoot { get; init; }
    public required string EmbeddingModelPath { get; init; }
    public required string EmbeddingMetadataPath { get; init; }
    public int WebpQuality { get; init; } = 75;
    public int MaxWidth { get; init; } = 123;
    public int GroupSize { get; init; } = 1000;
    public int DownloadParallelism { get; init; } = 2;
    public int BuildWorkers { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int Take { get; init; } = int.MaxValue;
    public bool Force { get; init; }
    public int RecordCounter;

    public string ImageDirectory => Path.Combine(OutputRoot, "images-webp");
    public string MetadataDirectory => Path.Combine(OutputRoot, "metadata");
    public string VectorDirectory => Path.Combine(OutputRoot, "vectors", "dotvector-shikiji-hnsw");
    public string RecordsPath => Path.Combine(MetadataDirectory, "records.jsonl");
    public string ManifestPath => Path.Combine(OutputRoot, "manifest.json");
    public string TarPath => OutputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".tar";

    public static PrebuildOptions Parse(string[] args)
    {
        var repoRoot = FullPath(GetValue(args, "--repo-root", Directory.GetCurrentDirectory()));
        var modelDir = FullPath(GetValue(args, "--model-dir", Path.Combine(repoRoot, ".agents", "dev_data", "models", "shikiji")));
        var datasetDir = FullPath(GetValue(args, "--dataset-data-dir", Path.Combine(repoRoot, ".agents", "dev_data", "datasets", "hi-utokyo-kuzushi", "data")));
        var outputRoot = FullPath(GetValue(args, "--output-root", Path.Combine(repoRoot, ".agents", "dev_data", "prebuilt", "kuzushi-shikiji-webp-dotvector")));

        var embeddingModelPath = FullPath(GetValue(
            args,
            "--embedding-model",
            Path.Combine(modelDir, "supervised_pretrain_checkpoint.embedding.onnx")));

        var embeddingMetadataPath = FullPath(GetValue(
            args,
            "--embedding-metadata",
            Path.Combine(modelDir, "supervised_pretrain_checkpoint.embedding.metadata.json")));

        return new PrebuildOptions
        {
            RepoRoot = repoRoot,
            ModelRepo = GetValue(args, "--model-repo", "kwadraten/shikiji"),
            DatasetRepo = GetValue(args, "--dataset-repo", "kwadraten/hi-utokyo-kuzushi"),
            ModelDirectory = modelDir,
            DatasetShardDirectory = datasetDir,
            OutputRoot = outputRoot,
            EmbeddingModelPath = embeddingModelPath,
            EmbeddingMetadataPath = embeddingMetadataPath,
            WebpQuality = GetInt(args, "--webp-quality", 75),
            MaxWidth = GetInt(args, "--max-width", 123),
            GroupSize = GetInt(args, "--group-size", 1000),
            DownloadParallelism = GetInt(args, "--download-parallelism", 2),
            BuildWorkers = GetInt(args, "--build-workers", Math.Max(1, Environment.ProcessorCount / 2)),
            Take = GetInt(args, "--take", int.MaxValue),
            Force = args.Contains("--force"),
        };
    }

    private static string GetValue(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private static int GetInt(string[] args, string name, int fallback) =>
        int.TryParse(GetValue(args, name, fallback.ToString()), out var value) && value > 0
            ? value
            : fallback;

    private static string FullPath(string path) => Path.GetFullPath(path);
}
