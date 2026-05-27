# Architecture

## Goal

Keep the app close to MVC while avoiding unnecessary project and layer count. The UI should stay thin, and model inference, downloads, dataset loading, caching, and similarity search should be testable without Avalonia controls.

## Project Shape

```text
KuzushiClassifierApp              # Shared Avalonia app
  Models/                         # Data models and small pure business rules
  Views/                          # Avalonia XAML and code-behind
  Controllers/                    # User-flow orchestration
  Services/                       # ONNX, HuggingFace, Parquet, cache, indexing
  Platform/                       # Platform abstractions used by shared code

KuzushiClassifierApp.Desktop      # Desktop startup and platform implementations
KuzushiClassifierApp.Android      # Android startup and platform implementations
```

Do not split into Domain/Application/Infrastructure projects unless the app grows enough to justify it.

## MVC Responsibilities

### Models

Models represent app data and stable business concepts.

Examples:

```text
KuzushiImage
KuzushiPrediction
KuzushiPredictionCandidate
SimilarImageResult
ImageEmbedding
ModelAssetStatus
DatasetImage
```

Rules:

- Models must not reference Avalonia controls.
- Models must not reference ONNX Runtime, HTTP clients, Parquet readers, or file-system APIs.
- Pure calculations such as confidence formatting, top-k candidate containers, or cosine similarity value objects can live here when they have no external dependencies.

### Views

Views are Avalonia XAML and code-behind files.

Rules:

- Views handle display, input events, and binding only.
- Views must not call ONNX Runtime, HuggingFace APIs, Parquet readers, or cache/index services directly.
- Views should delegate user actions to controllers.

### Controllers

Controllers coordinate user workflows and translate service results into view state.

Suggested controllers:

```text
StartupController
ClassificationController
SimilaritySearchController
```

Typical flow:

```text
image input -> preprocessing -> classification -> embedding -> similarity search -> results
```

Rules:

- Controllers may depend on services and models.
- Controllers should not implement model inference, file downloads, Parquet parsing, or low-level cache behavior.
- Controllers should expose simple async methods for the UI layer to call.

### Services

Services contain external integrations and heavier implementation details.

Suggested services:

```text
OnnxClassifierService
OnnxEmbeddingService
HuggingFaceDownloadService
ParquetImageLibraryService
EmbeddingIndexService
JsonFileEmbeddingCacheService
ImagePreprocessingService
```

Rules:

- Services may reference ONNX Runtime, HTTP, Parquet libraries, and file-system APIs.
- Services should return model types, not Avalonia controls.
- Services should hide platform-specific paths behind interfaces from `Platform/`.

### Platform

Platform abstractions isolate desktop and Android differences.

Examples:

```text
IAppDataPathProvider
IImagePicker
IPermissionService
```

Desktop and Android startup projects provide the concrete implementations.

## Dependency Direction

```text
Views -> Controllers -> Services
Controllers -> Models
Services -> Models
Services -> Platform abstractions
Desktop/Android -> platform implementations + app startup
```

Forbidden dependencies:

```text
Models -> Views
Models -> Services
Services -> Views
Views -> concrete ONNX/HuggingFace/Parquet implementations
```

## Startup Behavior

App startup should call a controller-level workflow, for example `StartupController.PrepareAsync()`.

That workflow should:

1. Check whether model and dataset assets are already cached.
2. Download missing assets from HuggingFace.
3. Load persisted image embeddings from the user's disk cache.
4. If the embedding cache is missing, stale, or invalid, calculate embeddings once and save them back to disk.
5. Load or build the in-memory embedding index from the persisted embeddings.
6. Report progress through model/state objects that the UI can display later.

Do not put this workflow directly inside `App.axaml.cs`, a View, or platform startup code.

## Local Development Cache

Large development-only assets live under `.agents/dev_data/` and are ignored by git.

Model files:

```text
.agents/dev_data/models/supervised_pretrain_checkpoint.onnx
.agents/dev_data/models/supervised_pretrain_checkpoint.embedding.onnx
```

The app runtime dataset cache is also shallow:

```text
.agents/dev_data/dataset/manifest.json
.agents/dev_data/dataset/metadata/records.jsonl
.agents/dev_data/dataset/images-webp/
.agents/dev_data/dataset/vectors/dotvector-shikiji-hnsw/
```

The app must treat `.agents/dev_data/prebuilt/` as a prebuilder output area only. It exists only in development and is not a runtime cache. Runtime app code must not directly read, copy from, or fall back to `.agents/dev_data/prebuilt/`; it should use the same download-and-extract path as packaged builds and cache the extracted package under `.agents/dev_data/dataset/`.

Downloaded HuggingFace Parquet shards used by `tools/KuzushiPrebuilder` live under:

```text
.agents/dev_data/dataset/data/*.parquet
```

`tools/KuzushiPrebuilder` writes publishable prebuilt package artifacts under `.agents/dev_data/prebuilt/`, but those artifacts are inputs for upload or release packaging, not an app-local shortcut.
