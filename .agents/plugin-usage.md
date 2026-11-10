# Kuzushi Classifier Patchouli plugin (usage)

`kwadraten.kuzushi-classifier` is a stdio MCP server that exposes the
existing KuzushiClassifierApp ONNX pipelines through the Patchouli plugin
contract. It performs on-device inference only, never writes to a library,
and keeps action results in memory for the lifetime of the plugin process
(bounded to 64 entries). Every runtime payload — ONNX models plus the glyph
embedding dataset — is a **host-managed file** declared as `[[files]]` in
`patchouli-plugin.toml`: the host downloads and verifies them into its own
managed store and passes their locations at launch. **The plugin process
never downloads anything** — no HuggingFace or COS calls exist in it.

## Managed files

The manifest declares five files under `[[files]]` (relative path +
`download_url`; `expected_bytes`/`sha256`/`version` are omitted because no
pinned constants exist for these payloads):

| declared path | source |
| --- | --- |
| `models/supervised_pretrain_checkpoint.onnx` | `https://huggingface.co/kwadraten/shikiji/resolve/main/supervised_pretrain_checkpoint.onnx` |
| `models/supervised_pretrain_checkpoint.metadata.json` | `…/resolve/main/supervised_pretrain_checkpoint.metadata.json` |
| `models/supervised_pretrain_checkpoint.embedding.onnx` | `…/resolve/main/supervised_pretrain_checkpoint.embedding.onnx` |
| `models/supervised_pretrain_checkpoint.embedding.metadata.json` | `…/resolve/main/supervised_pretrain_checkpoint.embedding.metadata.json` |
| `datasets/image-embeddings.with-webp.parquet` | `https://scripts-1303933394.cos.ap-tokyo.myqcloud.com/embeddings/image-embeddings.with-webp.parquet` |

## Launch

```bash
KuzushiClassifierApp.Plugin.exe [--data-root <path>] \
    [--patchouli-managed-files <root>] \
    [--patchouli-managed-file <relative>|<absolute>]...
```

- Transport: newline-delimited JSON-RPC 2.0 over stdio (no Content-Length
  framing). stdout carries protocol messages only; diagnostics go to stderr.
- The host injects the managed-file arguments: `--patchouli-managed-files`
  points at the managed root (declared files resolve as `{root}/{relative}`),
  and each `--patchouli-managed-file` pins one declared relative path to an
  explicit absolute location. An explicit mapping wins over the root.
- Without managed-file arguments — direct dev runs — asset resolution falls
  back to the app-data layout: the nearest `.agents/dev_data` above the
  working/base directory, otherwise `%LocalAppData%/KuzushiClassifierApp`.
  `--data-root` pins `{root}/models` + `{root}/datasets` explicitly for tests
  and isolated installs.
- The plugin only reads managed locations: it never creates directories under
  the managed root and never downloads. Missing declared files fail actions
  with `UNAVAILABLE` naming the declared relative paths and a pointer to the
  host's managed-files flow. Preparation runs in a single background job; its
  state is polled through `status.json`'s `preparation` object.

## Tools

`patchouli.plugin.find`

| argument | shape |
| --- | --- |
| `in` | `""` (root) or `"results/"`; anything else is `INVALID_ARGUMENT` |
| `query` | `""` browses; non-empty only inside `results/`, filters action/result-id |
| `limit` | integer 1..50, default 20 |
| `cursor` | opaque scope-bound continuation; malformed/foreign cursors are rejected |

`patchouli.plugin.fetch`

| uri | result |
| --- | --- |
| `""` | directory document (JSON string content) |
| `status.json` | readiness per asset group + capability (`schemas/status.json`) |
| `results/{result_id}.json` | immutable result (`schemas/result.json`); `NOT_FOUND` after restart/eviction |
| `results/` | `UNSUPPORTED_OPERATION` (find only) |
| `ui/main.html` | self-contained HTML document (`text/html`) backing the `main` view |

Traversal (`..`), percent escapes, query/fragment, OS paths and foreign
`patchouli://` namespaces are rejected with `INVALID_ARGUMENT`.

`status.json` never exposes absolute local paths: `assets.*.files[].path` and
`missing_assets[]` carry the manifest-declared relative paths, each file also
reports `managed` (whether it resolves to a host-injected location), and
`managed_files.enabled` reports whether the host injected managed locations at
launch. `downloads_performed` is always `false` — download state is the host's
own `files/` surface, not the plugin's.

`patchouli.plugin.send`

```json
{
  "endpoint": "control",
  "action": "classify | similar | analyze | prepare_assets | cancel_preparation",
  "target": "",
  "args": {
    "image_base64": "<required>",
    "media_type": "image/png | image/jpeg | image/webp (optional)",
    "top_k": "integer 1..50 (optional, default 10)"
  },
  "request_id": "optional string"
}
```

- `classify` needs only the classifier model+metadata; `similar` needs the
  embedding model+metadata and the declared embedding dataset; `analyze`
  needs all of them.
- `prepare_assets` takes `{allow_download: boolean}` (the only property).
  It synchronously starts (or reports) the single background preparation
  job and returns `{state:"completed", result:<preparation snapshot>}`.
  **The plugin performs a local load only — it never downloads.**
  `allow_download` is retained solely for host/UI orchestration
  compatibility: with any declared file missing the answer is always
  `needs_download`, and the files must be fetched through the host's
  managed-files surface first. With all files present the job scans the
  dataset, validates the embedding cache, builds the streaming similarity
  index and loads both ONNX sessions. `cancel_preparation` takes an empty
  args object and cancels the running job; a later `prepare_assets` may
  retry. The preparation snapshot is `{state: idle|needs_download|preparing|
  ready|failed|cancelled, message, fraction, models_loaded, index_loaded,
  error, downloads_performed, bytes_downloaded, total_bytes, items_processed,
  total_items}`; `models_loaded`/`index_loaded` reflect live runtime state,
  never file existence, and `downloads_performed` is always `false`.
- Decoded image bytes are capped at 8 MiB; decompressed dimensions are
  bounded from headers (max 8192 px/edge, 16,777,216 pixels) before decode.
- `request_id` deduplicates within the process (bounded): the same id plus
  the canonical same message replays the first immutable result; the same
  id with a different message is `REVISION_CONFLICT`.

Success: `{state:"completed", result:{result_id, action, resource_uri,
candidates, similar_images}}`. Errors: `isError=true` with
`{error:{code,message}}` — `INVALID_ARGUMENT`, `NOT_FOUND`, `UNAVAILABLE`,
`REVISION_CONFLICT`, `UNSUPPORTED_OPERATION`, `CANCELLED`.

`notifications/cancelled` with `params.requestId` cancels an in-flight
request; a clean stdin EOF drains pending requests and exits.

## Views

`patchouli-plugin.toml` declares one `[[ui.views]]` descriptor:

| field | value |
| --- | --- |
| `id` | `main` |
| `title` | `本地崩字识别` |
| `placement` | `window` (the host shows it in a standalone popup container) |
| `source` | `ui/main.html` (a `vfs.nodes` file node, `fetch` verb, `media = "text/html"`) |

The host composes `patchouli://plugin/kwadraten.kuzushi-classifier/views/main`
and loads the `source` document through `patchouli.plugin.fetch` with
`uri = "ui/main.html"`; the plugin returns
`{uri, content_type: "text/html", content}` where `content` is the complete
HTML string. The document is embedded in the plugin assembly
(`KuzushiClassifierApp.Plugin.Views.main.html`), so packaged zips need no
sidecar file, and it is fully self-contained under the host view CSP (inline
CSS/JS only, `img-src data:`, `connect-src 'none'`).

### View ↔ host bridge contract

The host exposes `window.patchouli.fetch(path)`,
`window.patchouli.send(path, action, args, requestId)` and
`window.patchouli.ensureFiles()` Promise APIs. The first two call *this
plugin's own* declared fetch/send surface through the host's permission-gated
bridge (`patchouli:plugin-request` → `patchouli:plugin-response`);
`ensureFiles()` asks the host to download/verify the declared `[[files]]`
managed files (added host-side with the managed-files service) and resolves
once the host reports them ready. The view uses them to:

- Poll `status.json` for real `assets.*`/`preparation` state; the header
  dots stay neutral until `preparation.models_loaded`/`index_loaded` are
  confirmed true — they never render a hardcoded green.
- Auto-run `prepare_assets {allow_download:false}` only when the declared
  files are all present; with missing files the primary button switches to
  下载依赖文件 and routes through `ensureManagedFiles()` (the host-side
  download, which shows its own consent/progress UI), then
  `prepare_assets {allow_download:false}` for the local load.
- Drive the predict button through `send("control","analyze",
  {image_base64, media_type, top_k:10})` with the uploaded image or the
  white-backed handwriting canvas, rendering candidates/similar rows with
  `textContent` only.
