# The data contract of a snapshot

A report job reads one snapshot and checks it before anything is rendered. When
the snapshot breaks the data contract the job ends `failed` with `DATA_INVALID`
before the first PDF page is rendered, and nothing of it is published: no
partition, no block, no artifact. The same is true wherever the violation sits —
in the header, in a middle record, or in the very last record of a full block.

## The shape of a snapshot

One UTF-8 JSON object, with exactly these three properties and in this order:

```json
{
  "contractVersion": "1.0",
  "header": { },
  "items": [ ]
}
```

- `contractVersion` — the string `1.0`. Nothing else is admitted.
- `header` — one object. A job's template decides which members it must carry.
- `items` — an array of objects, **possibly empty**. A missing `items` is a
  violation; an empty array is not.
- A single leading byte-order mark is skipped; a second one is not text of the
  profile at all.
- Whitespace after the root object is admitted. Anything else after it is a
  violation.

The shape is checked once, in one sequential pass that stops at the first
contract error. The header is read first; each record is then read, checked and
appended to the block; the end of the document is checked last, so a violation in
the last bytes of a large snapshot is still met by the job.

Every record, and the header, is then checked against the data schema of the
template version the job is pinned to. The schema is read from the taken copy of
that version and never from the template source, and it is compiled once per
package per worker.

## What a job reports

`error_reason` carries one line, built from positions and names and never from a
value of the document:

```text
the document cannot be used: code=DATA_INVALID reason=<reason> ordinal=<n> offset=<b>
the document cannot be used: code=DATA_INVALID reason=schema-violation ordinal=<n> offset=<b> path=<$.path> keyword=<k>
the document cannot be used: code=RECORD_TOO_LARGE limit=record ordinal=<n> offset=<b>
the document cannot be used: code=QUOTA_EXCEEDED limit=<limit> ordinal=<n> offset=<b>
```

- `ordinal` is the position of the value the pass stopped at: `0` is the header,
  and records are numbered from `1`. For a document cut inside a record it is
  the record the cut falls inside; between records it is the last complete
  record's number.
- `offset` is the byte of the uncompressed document.
- `path` is the field's path relative to the value that broke the schema, in the
  `$.name` form, and `keyword` is the rule that refused it. An unexpected member
  is reported at the object that carries it, and its name is never repeated.
- No value, member name, storage key, URL or host path appears anywhere in the
  message or in a log line.

The reasons of the profile itself:

| Reason | What it means |
|--------|---------------|
| `header-not-first` | The header comes after `items`, or the document has no header at all |
| `type-mismatch` | The header is not an object, or `items` is not an array |
| `duplicate-property` | A member is written twice in one object, decoded names compared |
| `profile-violation` | A fourth root member, a header before the version, a missing `contractVersion` or `items`, or an `items` element that is not an object |
| `contract-version` | A version other than `1.0` |
| `truncated` | The document ends before it is complete |
| `trailing-content` | A byte or a second value after the root object |
| `malformed` | The document is not valid UTF-8 JSON |
| `schema-violation` | A value does not satisfy the template's data schema |

A snapshot that breaks nothing is not refused: a valid document, an empty
`items` array, a single leading byte-order mark and trailing whitespace all pass
the check, and a job over them continues to printing.

## The validator and its version

The template's data schema is evaluated by `Corvus.Text.Json` **5.6.1**, the
version pinned in `Directory.Packages.props`. The schema language is JSON Schema
draft 2020-12, restricted to a profile that admits no identifier, no anchor, no
dynamic or recursive reference, no vocabulary declaration and no reference
outside the document itself. A version change is a planned change: the
conformance suite of the data-contract adapter is what it must pass.

A schema that cannot be used at all — absent from the taken copy, outside the
profile, not compilable, or too slow or too deep to decide a value — ends the job
`failed` with `TEMPLATE_INVALID` instead, in one attempt:

```text
TEMPLATE_INVALID file=schema.json rule=<rule or kind> [ordinal=<n>] [keyword=<k>]
TEMPLATE_INVALID rule=pin
```

`rule=pin` means the job's own template reference cannot be sourced: it names no
version (a reference by name, which nothing in the service resolves yet) or it
names a version that was never taken. The message names no file because the pin
path reads no schema.

The authoring side of the same contract — the dialect, the envelope and the
admitted keywords — is [template-authoring.md](template-authoring.md).
