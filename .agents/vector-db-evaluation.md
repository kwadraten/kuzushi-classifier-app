# Embedded Vector Database Evaluation

Date: 2026-05-26

## Scope

Evaluate DotVector and Qdrant Edge as replacements for the current single large JSON embedding cache.

The test reused the existing generated embedding cache:

```text
.agents/dev_data/datasets/hi-utokyo-kuzushi/image-embeddings.shikiji-768.v1.json
```

No embeddings were regenerated. A 10,000-record sample was streamed from the cache into ignored development data for repeatable local benchmarking.

## Environment

- OS: Windows
- App target: `net10.0` shared app, `net10.0` desktop, `net10.0-android` Android host
- Embedding dimension: 768
- Sample: first 10,000 records from the existing cache
- Query sanity check: first 100 sampled vectors queried against the built index

Generated benchmark artifacts were written under:

```text
.agents/dev_data/runtime/vector-benchmarks/
```

## Packages Checked

DotVector:

- NuGet package: `DotVector.Core` 1.0.0
- Target framework: `net10.0`
- Runtime dependency from nuspec: `System.Numerics.Tensors` 9.0.5
- Package description: embedded vector database engine with zero external runtime dependencies
- Project URL: <https://github.com/IoTSharp/DotVector>
- NuGet: <https://www.nuget.org/packages/DotVector.Core/1.0.0>

Qdrant Edge:

- Python package: `qdrant-edge-py` 0.6.1
- Windows install succeeded with wheel `cp310-abi3-win_amd64`
- PyPI also publishes desktop/server wheels for macOS, Linux, musl, x64, and arm64
- No .NET embedded binding was found in NuGet search; `Qdrant.Client` is for Qdrant server, not Qdrant Edge
- Docs: <https://qdrant.tech/documentation/edge/edge-quickstart/>
- PyPI: <https://pypi.org/project/qdrant-edge-py/>

## Results

| Engine | Insert 10k | Flush/Optimize | Query avg | Query p50 | Reload | Disk size | Self top-1 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| DotVector Flat | 0.36 s | 0.24 s flush | 19.93 ms | 2.87 ms | 0.17 s | 31.1 MB | 100/100 |
| DotVector HNSW | 4.79 s | 0.0005 s flush | 0.36 ms | 0.27 ms | 3.42 s | 31.8 MB | 100/100 |
| Qdrant Edge | 0.21 s | 0.19 s flush + 0.91 s optimize | 0.92 ms | 0.52 ms | 0.06 s | 202.2 MB | 100/100 |

Notes:

- DotVector Flat is exact and compact, but query latency is less suitable once the full dataset grows.
- DotVector HNSW has the best query latency and compact disk usage in this sample, but index build and reload are slower.
- Qdrant Edge is very smooth operationally and reloads quickly, but its persisted shard is much larger for this workload.
- All three tested modes passed the self-query sanity check on the first 100 vectors.

## Portability Assessment

DotVector is the better fit for this project if the choice is only between these two:

- It is directly consumable from the existing .NET business layer.
- It keeps the app single-process without a Python runtime or sidecar service.
- It has a small package/runtime surface and produced compact persisted data in the sample.
- It aligns with the existing MVC split: services can own a vector index abstraction without touching Views.

The main DotVector risks:

- The package is young and low-download compared with Qdrant.
- `DotVector.Core` currently targets `net10.0`; Android compatibility still needs an Android workload build test before adopting it in shared code.
- API maturity and on-disk compatibility should be treated as unstable until proven by upgrade tests.

Qdrant Edge is strong technically, but awkward for this Avalonia/.NET app today:

- The available embedded package is Python/Rust-facing, not .NET-facing.
- Using it from C# would require a Python runtime, a native bridge, or a sidecar process.
- That hurts Android packaging and makes desktop distribution heavier.
- The Qdrant ecosystem is more mature, but the mature .NET package is the network client for Qdrant server, not an embedded edge database.

## Recommendation

Adopt a small internal vector-store interface first, then implement a DotVector-backed provider behind it.

Suggested interface boundary:

```text
IEmbeddingVectorStore
  EnsureReadyAsync(...)
  UpsertAsync(...)
  SearchAsync(...)
  GetStatusAsync(...)
```

Keep the current JSON cache only as a migration/import source during development. Persist the vector database under the user's app data directory, with a cache key derived from:

- embedding model id/version
- embedding dimension
- dataset fingerprint
- vector database provider/version

Do not adopt Qdrant Edge for the in-app embedded cache unless a supported .NET binding appears or the project intentionally moves to a local service architecture.

## Pre-Embedded Developer Dataset Strategy

Both DotVector and Qdrant Edge can persist vectors together with payload metadata.

Payload smoke test:

- DotVector persisted and reloaded `label`, `image_path`, and `image_base64` through `VectorRecord.Payload`.
- Qdrant Edge persisted and reloaded the same fields through point `payload`.

This means the vector store can safely own the lookup record:

```text
id
label
embedding vector
image logical path
dataset split/source metadata
optional small preview metadata
```

It should not own full image binaries for this app.

Reasons:

- Full image payloads force every vector index backup, migration, and rebuild to carry image bytes.
- Payload values are effectively structured metadata; raw images need base64 encoding, which increases size and adds decode overhead.
- Image access patterns differ from vector search: search needs fast vector reads, while UI result rendering needs a few selected images.
- Keeping images separate lets the app later switch vector providers without rewriting the image asset format.

Recommended developer-side pre-embedding package:

```text
prebuilt/
  manifest.json
  vectors/
    dotvector-shikiji-768-v1/...
  images/
    train/...
  metadata/
    records.parquet or records.jsonl
```

`manifest.json` should include:

```text
dataset id/version/fingerprint
embedding model id/version
embedding dimension
vector provider/version/index kind
image asset format/version
record count
created timestamp
```

Runtime flow:

1. Check the manifest against local app cache state.
2. If the prebuilt vector store matches, copy or memory-map it into the user's app data cache.
3. If missing or stale, rebuild the vector store from packaged embeddings and metadata.
4. Load images lazily by `image_path` only for visible/search-result records.

For this project, treat the vector database as the index plus metadata lookup, not as the canonical image archive.
