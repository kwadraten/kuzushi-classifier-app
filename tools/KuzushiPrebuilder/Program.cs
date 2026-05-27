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
using Spectre.Console;

var options = PrebuildOptions.Parse(args);
await new PrebuildPipeline(options).RunAsync();

internal sealed class PrebuildPipeline(PrebuildOptions options)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = CreateHttpClient();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public async Task RunAsync()
    {
        PrintConfiguration();
        PrepareOutputDirectories();

        Console.WriteLine();
        Console.WriteLine("阶段 1/4：下载模型和数据集分片");
        var shardPaths = await DownloadStageAsync();

        Console.WriteLine();
        Console.WriteLine("阶段 2/4：压缩图片");
        var targetCount = await CountTargetRowsAsync(shardPaths);
        await CompressStageAsync(shardPaths, targetCount);

        Console.WriteLine();
        Console.WriteLine("阶段 3/4：ONNX 嵌入并写入 DotVector");
        var metadata = LoadEmbeddingMetadata(options.EmbeddingMetadataPath);
        var records = LoadCompressedRecords();
        await EmbedAndIndexStageAsync(metadata, records);

        Console.WriteLine();
        Console.WriteLine("阶段 4/4：打包 tar");
        CreateTarPackage();

        Console.WriteLine();
        Console.WriteLine($"完成，用时 {_clock.Elapsed.TotalMinutes:F1} 分钟。");
    }

    private void PrintConfiguration()
    {
        Console.WriteLine($"repo root: {options.RepoRoot}");
        Console.WriteLine($"model dir: {options.ModelDirectory}");
        Console.WriteLine($"dataset shard dir: {options.DatasetShardDirectory}");
        Console.WriteLine($"output root: {options.OutputRoot}");
        Console.WriteLine($"download parallelism: {options.DownloadParallelism}");
        Console.WriteLine($"cpu workers: {options.BuildWorkers}");
        Console.WriteLine($"group size: {options.GroupSize}");
        Console.WriteLine($"webp quality: {options.WebpQuality}");
        Console.WriteLine($"max width: {options.MaxWidth}");
        if (options.Take != int.MaxValue)
        {
            Console.WriteLine($"take: {options.Take}");
        }
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
        Directory.CreateDirectory(options.VectorDirectory);
        Directory.CreateDirectory(options.ModelDirectory);
        Directory.CreateDirectory(options.DatasetShardDirectory);
    }

    private async Task<string[]> DownloadStageAsync()
    {
        var modelItems = PrebuildOptions.ModelFiles
            .Select(fileName => new DownloadItem(
                $"https://huggingface.co/{options.ModelRepo}/resolve/main/{fileName}",
                Path.Combine(options.ModelDirectory, fileName),
                fileName))
            .ToArray();

        var shardRelativePaths = await ListDatasetShardsAsync();
        var shardItems = shardRelativePaths
            .Select(path => new DownloadItem(
                $"https://huggingface.co/datasets/{options.DatasetRepo}/resolve/main/{path}",
                Path.Combine(options.DatasetShardDirectory, Path.GetFileName(path)),
                path))
            .ToArray();

        var allItems = modelItems.Concat(shardItems).ToArray();
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(CreateProgressColumns())
            .StartAsync(async context =>
        {
            var task = context.AddTask("下载", maxValue: allItems.Length);

            await RunBoundedAsync(allItems, options.DownloadParallelism, async item =>
            {
                await DownloadIfMissingAsync(item);
                task.Increment(1);
            });
        });

        return shardItems.Select(item => item.LocalPath).Order().ToArray();
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
            Console.WriteLine($"无法从 Hugging Face API 列出数据集分片，使用默认分片名：{ex.Message}");
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

    private async Task DownloadIfMissingAsync(DownloadItem item)
    {
        if (FileReady(item.LocalPath))
        {
            return;
        }

        var tempPath = item.LocalPath + ".download";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using var response = await _httpClient.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 20,
            useAsync: true);

        var buffer = new byte[1 << 20];
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read));
        }

        await target.FlushAsync();
        File.Move(tempPath, item.LocalPath, overwrite: true);
    }

    private async Task<int> CountTargetRowsAsync(string[] shardPaths)
    {
        var total = 0L;

        foreach (var shardPath in shardPaths)
        {
            await using var fs = File.OpenRead(shardPath);
            await using var reader = await ParquetReader.CreateAsync(fs);

            for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
            {
                using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
                total += rowGroupReader.RowCount;
            }
        }

        return (int)Math.Min(total, options.Take);
    }

    private async Task CompressStageAsync(string[] shardPaths, int targetCount)
    {
        if (!options.Force && TryLoadCompleteCompressedRecords(targetCount, out var existingRecords))
        {
            Console.WriteLine($"跳过压缩：已存在 {existingRecords.Length:N0} 条 metadata，图片文件完整。");
            return;
        }

        if (File.Exists(options.RecordsPath))
        {
            File.Delete(options.RecordsPath);
        }

        var completed = 0;
        var accepted = 0;
        var writerLock = new object();

        await using var recordStream = new FileStream(
            options.RecordsPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            1 << 20,
            useAsync: false);
        using var recordWriter = new StreamWriter(recordStream);

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(CreateProgressColumns())
            .StartAsync(async context =>
        {
            var task = context.AddTask("压缩", maxValue: targetCount);

            await ParallelForEachAsync(shardPaths, options.BuildWorkers, shardPath =>
            {
                CompressShard(shardPath, recordWriter, writerLock, task, ref completed, ref accepted);
            });
        });

        Console.WriteLine($"压缩完成，共 {accepted:N0} 张");
    }

    private void CompressShard(
        string shardPath,
        StreamWriter recordWriter,
        object writerLock,
        ProgressTask progress,
        ref int completed,
        ref int accepted)
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
                    var sequence = Interlocked.Increment(ref completed);
                    if (sequence > options.Take)
                    {
                        return;
                    }

                    var bytes = imageBytes[row];
                    if (bytes is null || bytes.Length == 0)
                    {
                        progress.Increment(1);
                        continue;
                    }

                    var id = BuildDeterministicId(shardPath, rowGroupIndex, row);
                    var record = CompressImage(shardPath, rowGroupIndex, row, id, labels[row] ?? "", bytes);
                    Interlocked.Increment(ref accepted);

                    lock (writerLock)
                    {
                        recordWriter.WriteLine(JsonSerializer.Serialize(record, WebJson));
                    }

                    progress.Increment(1);
                }
            }
        }
        finally
        {
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private CompressedRecord CompressImage(
        string shardPath,
        int rowGroupIndex,
        int row,
        string id,
        string label,
        byte[] bytes)
    {
        using var image = Image.Load<Rgba32>(bytes);
        var rowGroupMarker = id.IndexOf("_rg", StringComparison.Ordinal);
        var bucket = rowGroupMarker > 0 ? id[..rowGroupMarker] : "unknown";
        var relativePath = Path.Combine("images-webp", bucket, id + ".webp").Replace('\\', '/');
        var absolutePath = Path.Combine(options.OutputRoot, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        var compressedWidth = image.Width;
        var compressedHeight = image.Height;

        if (!options.Force && FileReady(absolutePath))
        {
            if (TryIdentifyImage(absolutePath, out var existingWidth, out var existingHeight))
            {
                return new CompressedRecord(
                    id,
                    label,
                    relativePath,
                    Path.GetFileName(shardPath),
                    rowGroupIndex,
                    row,
                    image.Width,
                    image.Height,
                    existingWidth,
                    existingHeight);
            }

            File.Delete(absolutePath);
        }

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

        return new CompressedRecord(
            id,
            label,
            relativePath,
            Path.GetFileName(shardPath),
            rowGroupIndex,
            row,
            image.Width,
            image.Height,
            compressedWidth,
            compressedHeight);
    }

    private CompressedRecord[] LoadCompressedRecords()
    {
        if (!File.Exists(options.RecordsPath))
        {
            throw new FileNotFoundException("Compressed metadata file was not found.", options.RecordsPath);
        }

        return File.ReadLines(options.RecordsPath)
            .Select(line => JsonSerializer.Deserialize<CompressedRecord>(line, WebJson))
            .Where(record => record is not null)
            .Cast<CompressedRecord>()
            .OrderBy(record => record.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task EmbedAndIndexStageAsync(EmbeddingMetadata metadata, CompressedRecord[] records)
    {
        if (!options.Force && IsExistingVectorIndexComplete(records.Length))
        {
            Console.WriteLine($"跳过嵌入：DotVector 索引已存在，manifest 记录数为 {records.Length:N0}。");
            return;
        }

        if (Directory.Exists(options.VectorDirectory))
        {
            Directory.Delete(options.VectorDirectory, recursive: true);
        }

        Directory.CreateDirectory(options.VectorDirectory);

        using var db = new VectorDatabase(options.VectorDirectory);
        var collection = db.CreateCollection<string>(
            "images",
            768,
            Metric.Cosine,
            IndexKind.Hnsw,
            HnswOptions.Default);

        var output = new BlockingCollection<EmbeddedRecord>(options.GroupSize * options.BuildWorkers);
        var writerTask = Task.Run(() => WriteEmbeddingBatches(db, collection, output, metadata, records.Length));
        var recordLookup = records.ToDictionary(
            record => SourceKey(record.ShardFile, record.RowGroup, record.Row),
            StringComparer.Ordinal);

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(CreateProgressColumns())
            .StartAsync(async context =>
            {
                var task = context.AddTask("嵌入", maxValue: records.Length);

                await ParallelForEachAsync(
                    records.GroupBy(record => record.ShardFile).Select(group => group.Key).ToArray(),
                    options.BuildWorkers,
                    shardFile =>
                    {
                        using var session = new InferenceSession(options.EmbeddingModelPath);
                        EmbedShard(shardFile, recordLookup, session, metadata, output, task);
                    });
            });

        output.CompleteAdding();
        await writerTask;
    }

    private void EmbedShard(
        string shardFile,
        IReadOnlyDictionary<string, CompressedRecord> recordLookup,
        InferenceSession session,
        EmbeddingMetadata metadata,
        BlockingCollection<EmbeddedRecord> output,
        ProgressTask progress)
    {
        var shardPath = Path.Combine(options.DatasetShardDirectory, shardFile);

        using var fs = File.OpenRead(shardPath);
        var reader = ParquetReader.CreateAsync(fs).GetAwaiter().GetResult();

        try
        {
            var imageField = FindField(reader, "image", "bytes");

            for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
            {
                using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
                var rowCount = (int)rowGroupReader.RowCount;
                var imageBytes = new byte[]?[rowCount];
                rowGroupReader.ReadAsync(imageField, imageBytes.AsMemory()).AsTask().GetAwaiter().GetResult();

                for (var row = 0; row < rowCount; row++)
                {
                    var key = SourceKey(shardFile, rowGroupIndex, row);
                    if (!recordLookup.TryGetValue(key, out var record))
                    {
                        continue;
                    }

                    var bytes = imageBytes[row];
                    if (bytes is null || bytes.Length == 0)
                    {
                        continue;
                    }

                    using var image = Image.Load<Rgba32>(bytes);
                    var vector = RunEmbedding(session, metadata, image);
                    output.Add(new EmbeddedRecord(record, vector));
                    progress.Increment(1);
                }
            }
        }
        finally
        {
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void WriteEmbeddingBatches(
        VectorDatabase db,
        Collection<string> collection,
        BlockingCollection<EmbeddedRecord> output,
        EmbeddingMetadata metadata,
        int total)
    {
        var batch = new List<EmbeddedRecord>(options.GroupSize);
        var stored = 0;

        foreach (var record in output.GetConsumingEnumerable())
        {
            batch.Add(record);
            if (batch.Count >= options.GroupSize)
            {
                StoreBatch(collection, batch);
                stored += batch.Count;
                batch.Clear();
                db.Flush();
            }
        }

        if (batch.Count > 0)
        {
            StoreBatch(collection, batch);
            stored += batch.Count;
        }

        db.Flush();
        WriteManifest(metadata, stored);

        if (stored != total)
        {
            Console.WriteLine($"警告：metadata 有 {total:N0} 条，DotVector 写入 {stored:N0} 条。");
        }
    }

    private static void StoreBatch(Collection<string> collection, List<EmbeddedRecord> batch)
    {
        var vectors = batch.Select(item =>
        {
            var record = item.Record;
            var payload = new Dictionary<string, object>
            {
                ["label"] = record.Label,
                ["image_file"] = record.ImageFile,
                ["original_width"] = record.OriginalWidth,
                ["original_height"] = record.OriginalHeight,
                ["compressed_width"] = record.CompressedWidth,
                ["compressed_height"] = record.CompressedHeight,
            };

            return new VectorRecord<string>(record.Id, item.Vector)
            {
                Payload = payload,
            };
        }).ToArray();

        collection.InsertBatch(vectors);
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

    private bool TryLoadCompleteCompressedRecords(int targetCount, out CompressedRecord[] records)
    {
        records = [];

        if (!File.Exists(options.RecordsPath))
        {
            return false;
        }

        try
        {
            records = LoadCompressedRecords();
        }
        catch
        {
            records = [];
            return false;
        }

        if (records.Length != targetCount)
        {
            return false;
        }

        return records.All(record =>
        {
            var imagePath = Path.Combine(options.OutputRoot, record.ImageFile.Replace('/', Path.DirectorySeparatorChar));
            return FileReady(imagePath) && TryIdentifyImage(imagePath, out _, out _);
        });
    }

    private bool IsExistingVectorIndexComplete(int expectedRecordCount)
    {
        if (!File.Exists(options.ManifestPath) || !Directory.Exists(options.VectorDirectory))
        {
            return false;
        }

        if (!Directory.EnumerateFiles(options.VectorDirectory, "*", SearchOption.AllDirectories).Any())
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(options.ManifestPath));
            if (!doc.RootElement.TryGetProperty("recordCount", out var recordCount))
            {
                return false;
            }

            return recordCount.GetInt32() == expectedRecordCount;
        }
        catch
        {
            return false;
        }
    }

    private void CreateTarPackage()
    {
        if (File.Exists(options.TarPath))
        {
            File.Delete(options.TarPath);
        }

        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(CreateProgressColumns())
            .Start(context =>
            {
                var task = context.AddTask("打包", maxValue: 1);
                TarFile.CreateFromDirectory(options.OutputRoot, options.TarPath, includeBaseDirectory: false);
                task.Increment(1);
            });

        Console.WriteLine($"打包完成：{options.TarPath}");
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

    private static string BuildDeterministicId(string shardPath, int rowGroupIndex, int row)
    {
        var shardName = Path.GetFileNameWithoutExtension(shardPath)
            .Replace('-', '_')
            .Replace('.', '_');

        return $"{shardName}_rg{rowGroupIndex:D3}_row{row:D6}";
    }

    private static string SourceKey(string shardFile, int rowGroup, int row) =>
        $"{shardFile}|{rowGroup}|{row}";

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

    private static async Task ParallelForEachAsync<T>(IReadOnlyCollection<T> items, int parallelism, Action<T> action)
    {
        using var semaphore = new SemaphoreSlim(parallelism);
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                await Task.Run(() => action(item));
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

    private static bool TryIdentifyImage(string path, out int width, out int height)
    {
        try
        {
            var info = Image.Identify(path);
            width = info.Width;
            height = info.Height;
            return true;
        }
        catch
        {
            width = 0;
            height = 0;
            return false;
        }
    }

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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(60),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KuzushiPrebuilder", "1.0"));
        return client;
    }

    private static ProgressColumn[] CreateProgressColumns() =>
    [
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new RemainingTimeColumn(),
        new SpinnerColumn(),
    ];
}

internal sealed record DownloadItem(string Url, string LocalPath, string Label);

internal sealed record CompressedRecord(
    string Id,
    string Label,
    string ImageFile,
    string ShardFile,
    int RowGroup,
    int Row,
    int OriginalWidth,
    int OriginalHeight,
    int CompressedWidth,
    int CompressedHeight);

internal sealed record EmbeddedRecord(CompressedRecord Record, float[] Vector);

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
    public int DownloadParallelism { get; init; } = 4;
    public int BuildWorkers { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int Take { get; init; } = int.MaxValue;
    public bool Force { get; init; }

    public string ImageDirectory => Path.Combine(OutputRoot, "images-webp");
    public string MetadataDirectory => Path.Combine(OutputRoot, "metadata");
    public string VectorDirectory => Path.Combine(OutputRoot, "vectors", "dotvector-shikiji-hnsw");
    public string RecordsPath => Path.Combine(MetadataDirectory, "records.jsonl");
    public string ManifestPath => Path.Combine(OutputRoot, "manifest.json");
    public string TarPath => OutputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".tar";

    public static PrebuildOptions Parse(string[] args)
    {
        var repoRoot = FullPath(GetValue(args, "--repo-root", Directory.GetCurrentDirectory()));
        var modelDir = FullPath(GetValue(args, "--model-dir", Path.Combine(repoRoot, ".agents", "dev_data", "models")));
        var datasetDir = FullPath(GetValue(args, "--dataset-data-dir", Path.Combine(repoRoot, ".agents", "dev_data", "dataset", "data")));
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
            DownloadParallelism = GetInt(args, "--download-parallelism", 4),
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
