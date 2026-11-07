# Kuzushi Classifier Patchouli plugin (usage)

`kwadraten.kuzushi-classifier` is a stdio MCP server that exposes the
existing KuzushiClassifierApp ONNX pipelines through the Patchouli plugin
contract. It performs on-device inference only; it never downloads assets,
never writes to a library, and keeps action results in memory for the
lifetime of the plugin process (bounded to 64 entries).

## Launch

```bash
KuzushiClassifierApp.Plugin.exe [--data-root <path>]
```

- Transport: newline-delimited JSON-RPC 2.0 over stdio (no Content-Length
  framing). stdout carries protocol messages only; diagnostics go to stderr.
- Asset resolution without `--data-root` matches the standalone app: the
  nearest `.agents/dev_data` above the working/base directory, otherwise
  `%LocalAppData%/KuzushiClassifierApp`. `--data-root` pins
  `{root}/models` + `{root}/datasets` explicitly for tests and isolated
  installs.
- Missing cached models or dataset fail actions with `UNAVAILABLE` and
  precise file paths. Prepare assets by running the standalone app once;
  there is no prepare/download action in this plugin.

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

Traversal (`..`), percent escapes, query/fragment, OS paths and foreign
`patchouli://` namespaces are rejected with `INVALID_ARGUMENT`.

`patchouli.plugin.send`

```json
{
  "endpoint": "control",
  "action": "classify | similar | analyze",
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
  embedding model+metadata and the cached embedding dataset; `analyze`
  needs all of them.
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
