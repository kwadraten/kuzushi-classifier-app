# Image Compression Evaluation

Date: 2026-05-26

## Scope

Test whether converting dataset images to small WebP/PNG assets reduces the size of a developer-side prebuilt image package.

The test used real images streamed from:

```text
.agents/dev_data/datasets/hi-utokyo-kuzushi/data/*.parquet
```

No embeddings were regenerated.

## Dataset Count

```text
train-00000-of-00005.parquet rows=65,053
train-00001-of-00005.parquet rows=65,052
train-00002-of-00005.parquet rows=65,052
train-00003-of-00005.parquet rows=65,052
train-00004-of-00005.parquet rows=65,052
total_rows=325,261
```

## 10,000 Image Sample

Settings:

- Resize: keep aspect ratio, do not enlarge, `max(width,height) <= 224`
- Encoder: ImageSharp 3.1.12
- Source: first 10,000 non-empty image records from the Parquet dataset

Results:

| Variant | Total | Average | Ratio vs source PNG bytes | Avg encode time |
| --- | ---: | ---: | ---: | ---: |
| Source PNG bytes | 37.15 MiB | 3,895 bytes | 100.0% | n/a |
| WebP q70 | 15.30 MiB | 1,605 bytes | 41.2% | 6.07 ms/image |
| WebP q80 | 19.05 MiB | 1,998 bytes | 51.3% | 6.29 ms/image |
| WebP lossless | 67.04 MiB | 7,030 bytes | 180.5% | 12.20 ms/image |
| PNG best compression | 120.19 MiB | 12,602 bytes | 323.5% | 12.13 ms/image |

The original sampled images are already small:

```text
avg_width=122.7
avg_height=145.7
max_side_max=1775
```

So the space saving comes mostly from lossy WebP, not only from resizing.

## No-Resize Control

The resize step only shrinks images with a side longer than 224 pixels; it does not enlarge smaller images.

On the same 10,000 image sample, 1,658 images had a side longer than 224 pixels.

| Variant | Total | Average | Ratio vs source PNG bytes | Avg encode time |
| --- | ---: | ---: | ---: | ---: |
| Source PNG bytes | 37.15 MiB | 3,895 bytes | 100.0% | n/a |
| WebP q60, no resize | 16.14 MiB | 1,693 bytes | 43.5% | 8.66 ms/image |
| WebP q70, no resize | 17.88 MiB | 1,875 bytes | 48.1% | 8.78 ms/image |
| WebP q80, no resize | 22.22 MiB | 2,330 bytes | 59.8% | 9.05 ms/image |

Compared with `max_side=224`, no-resize WebP is larger at the same quality level:

| Variant | With max_side=224 | No resize |
| --- | ---: | ---: |
| WebP q70 | 15.30 MiB | 17.88 MiB |
| WebP q80 | 19.05 MiB | 22.22 MiB |

So removing the resize step does not further reduce package size. It increases size because the long-tail large images remain large.

## Full Dataset Estimate

Using the 10,000 image sample average against 325,261 rows:

| Variant | Estimated full image bytes |
| --- | ---: |
| Source PNG bytes | 1,208 MiB |
| WebP q70 | 498 MiB |
| WebP q80 | 620 MiB |
| WebP q60, no resize | 525 MiB |
| WebP q70, no resize | 582 MiB |
| WebP q80, no resize | 723 MiB |

This estimate covers image bytes only. It does not include vector index files, metadata, manifests, or archive overhead.

## Recommendation

Use WebP for the prebuilt local image package.

Default choice:

```text
max_side=224
format=webp
quality=70 or 75
```

Avoid recompressing to PNG and avoid WebP lossless for this dataset. Both increased size in the sample because the source PNGs are already highly optimized and the resized output becomes less palette-friendly.

For the app architecture:

- Store `image_path`, dimensions, and format in vector payload/metadata.
- Keep compressed image files outside the vector database.
- Load images lazily from the local image pack after vector search returns ids.
- Keep original Parquet/data source separate for development or rebuild workflows, not for normal user runtime.
