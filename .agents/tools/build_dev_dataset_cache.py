#!/usr/bin/env python
"""Build a local development cache from HuggingFace Parquet shards.

The C# app reads the generated cache instead of parsing Parquet directly:

  .agents/dev_data/datasets/hi-utokyo-kuzushi/cache/metadata.jsonl
  .agents/dev_data/datasets/hi-utokyo-kuzushi/cache/images/*.jpg
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import pyarrow.parquet as pq


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--dataset-root",
        type=Path,
        default=Path(".agents/dev_data/datasets/hi-utokyo-kuzushi"),
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=0,
        help="Maximum rows to extract. Use 0 for all rows.",
    )
    args = parser.parse_args()

    dataset_root = args.dataset_root
    parquet_dir = dataset_root / "data"
    cache_dir = dataset_root / "cache"
    image_dir = cache_dir / "images"
    metadata_path = cache_dir / "metadata.jsonl"

    image_dir.mkdir(parents=True, exist_ok=True)

    parquet_files = sorted(parquet_dir.glob("*.parquet"))
    if not parquet_files:
        raise FileNotFoundError(f"No parquet files found in {parquet_dir}")

    count = 0
    with metadata_path.open("w", encoding="utf-8") as metadata:
        for parquet_file in parquet_files:
            table = pq.read_table(parquet_file, columns=["image", "char", "unicode"])
            image_column = table["image"]
            char_column = table["char"]
            unicode_column = table["unicode"]

            for row_index in range(table.num_rows):
                if args.limit and count >= args.limit:
                    print(f"Extracted {count} rows to {cache_dir}")
                    return

                image = image_column[row_index].as_py()
                image_bytes = image["bytes"]
                original_path = image["path"] or f"{parquet_file.stem}-{row_index}.jpg"
                label = char_column[row_index].as_py()
                unicode_value = unicode_column[row_index].as_py()

                digest = hashlib.sha256(image_bytes).hexdigest()[:16]
                extension = Path(original_path).suffix or ".jpg"
                image_file_name = f"{count:08d}-{digest}{extension}"
                image_path = image_dir / image_file_name
                image_path.write_bytes(image_bytes)

                record = {
                    "id": f"{parquet_file.stem}:{row_index}",
                    "label": label,
                    "unicode": unicode_value,
                    "sourcePath": original_path,
                    "localPath": str(image_path.relative_to(cache_dir)).replace("\\", "/"),
                }
                metadata.write(json.dumps(record, ensure_ascii=False) + "\n")
                count += 1

    print(f"Extracted {count} rows to {cache_dir}")


if __name__ == "__main__":
    main()
