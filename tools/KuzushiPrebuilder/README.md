# Kuzushi Prebuilder

Developer-only console tool for building an uploadable pre-embedded local search package.

The tool:

- downloads the latest Shikiji ONNX model files and Kuzushi parquet shards from Hugging Face;
- skips existing non-empty files to avoid repeated downloads;
- starts processing each dataset shard as soon as that shard is downloaded while other shards continue downloading;
- converts images to WebP with an aggressive default of `quality=75` and `max-width=123`;
- runs the ONNX embedding model locally;
- stores embeddings, labels, and image file names into a DotVector HNSW index in groups of 1000;
- writes metadata and packages the final output as `.tar`.

Default paths match the existing development layout under `.agents/dev_data`, and generated package output remains ignored by git.

## Quick Test

```powershell
dotnet run --project tools\KuzushiPrebuilder\KuzushiPrebuilder.csproj -- `
  --repo-root . `
  --take 10 `
  --force
```

## Full Build

```powershell
dotnet run --project tools\KuzushiPrebuilder\KuzushiPrebuilder.csproj -c Release -- `
  --repo-root . `
  --download-parallelism 2 `
  --build-workers 4 `
  --group-size 1000 `
  --webp-quality 75 `
  --max-width 123 `
  --force
```

The default `--max-width 123` comes from the measured average image width in the local compression evaluation. Use `--max-width` explicitly if the dataset changes and a new target is desired.

## Output

Default output:

```text
.agents/dev_data/prebuilt/kuzushi-shikiji-webp-dotvector/
  manifest.json
  metadata/records.jsonl
  images-webp/
  vectors/dotvector-shikiji-hnsw/

.agents/dev_data/prebuilt/kuzushi-shikiji-webp-dotvector.tar
```

The tar file is the upload candidate.

