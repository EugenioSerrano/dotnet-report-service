# Writing a template

A template is a package: a manifest, the HTML parts its document is rendered
from, and the data schema its reports are checked against. This page covers what
a template author writes and what the service does with it.

## The package

```text
<templateId>/
  template.yaml        the manifest
  schema.json          the data contract of the reports
  html/…               the parts of the HTML document
```

The manifest declares the template's identifier, its version, the engine that
renders it, the engine's version, and the data contract: its profile
(`header-items-v1`), the profile's version (`1.0`) and the package-relative path
of the schema. A version is taken once: the service reads the package at one
commit, keeps it as one write-once object and records what it fixed. A job is
then pinned to the version it named and reads only that stored copy.

## The data schema (the data-contract section)

`schema.json` is one JSON Schema document. It is the **envelope** schema of the
reports: the same document declares the header and the record, and the service
evaluates those two subschemas against every job's data:

- `properties.header` — the document's header;
- `properties.items.items` — one record.

Both must exist, directly or behind a `$ref` inside the document. Nothing else
of the envelope is evaluated at job time: an `items` keyword such as `minItems`
describes the envelope, not a job's data, and an empty `items` array is admitted
whatever the envelope says.

### The dialect and the profile

**Draft 2020-12** (`"$schema": "https://json-schema.org/draft/2020-12/schema"`).
The profile admits:

- the assertion and annotation keywords of the draft that apply to a value —
  `type`, `enum`, `const`, `required`, `properties`,
  `additionalProperties`, `unevaluatedProperties`, `propertyNames`, `items`,
  `prefixItems`, `contains`, `minContains`, `maxContains`, `minItems`,
  `maxItems`, `uniqueItems`, `minLength`, `maxLength`, `pattern`, `minimum`,
  `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`,
  `dependentRequired`, `dependentSchemas`, `format`, `not`, `allOf`, `anyOf`,
  `oneOf`, `if`/`then`/`else`, and the boolean schemas;
- `$schema` at the root, `$ref` to a fragment of the same document, `$defs` and
  `$comment`.

It refuses, by naming the rule and the place inside the schema:

| Rule | What it refuses |
|------|-----------------|
| `size` | A schema larger than 65 536 bytes |
| `schema` | A root without the draft 2020-12 `$schema` |
| `keyword` | `$id`, `$anchor`, `$dynamicRef`, `$dynamicAnchor`, `$recursiveRef`, `$recursiveAnchor`, `$vocabulary`, or any other member the profile does not admit |
| `depth` | A schema nested deeper than 64 schema positions |
| `pattern` | A `pattern` or `patternProperties` key longer than 1 024 characters, or not a valid ECMA-262 pattern |
| `format` | A `format` name outside the nine the service asserts |
| `reference` | A `$ref` that is not a fragment of the document, or one that does not resolve |
| `cycle` | A reference cycle that never descends into the value, so it could never terminate |
| `envelope` | A document without `properties.header` or `properties.items.items` |
| `compile` | A document the evaluator cannot compile |

**No reference leaves the document.** A remote or a file reference is refused at
load time; the service resolves nothing over the network and reads nothing from
the file system.

### Formats

`date`, `date-time`, `time`, `duration`, `email`, `uri`, `uuid`, `ipv4`,
`ipv6`. Each is asserted: a value that does not conform fails the job. An
unknown format name is refused by the profile rather than ignored. At the pinned
validator, `time` requires an offset (`13:45:30Z` is admitted, `13:45:30` is
not).

### Ceilings

| What | Ceiling |
|------|---------|
| The schema document | 65 536 bytes |
| Nesting of the schema | 64 schema positions |
| One pattern | 1 024 characters |
| One match of a pattern | 1 second, then the job ends `TEMPLATE_INVALID` |
| Nesting of a value | 64, then the value cannot be decided |
| One record | 1 MiB (the reader's own limit) |

The evaluator parses the schema with its own bound as well, and that bound is
lower than 64 positions: a schema nested past it is refused as `compile` rather
than admitted, so a document too deep to evaluate never reaches a job. Keep a
schema well inside 24 positions.

A pattern near the one-second limit can decide differently on a slower machine;
a pattern written to match in ordinary time is the safe choice.

## What the service does with it

The header is checked against the header subschema and every record against the
record subschema, record by record, in the pass that reads the snapshot and
before any page is rendered. A value that breaks the schema ends the job
`failed` with `DATA_INVALID` and a diagnostic that names the ordinal, the byte
offset, the field's path relative to the value and the rule — never the value:

```text
the document cannot be used: code=DATA_INVALID reason=schema-violation ordinal=2 offset=1043 path=$.amount keyword=type
```

A schema that cannot be used at all ends the job with `TEMPLATE_INVALID` instead,
in one attempt, and the message names `schema.json` and the rule. The reasons, the
shape of a snapshot and the whole diagnostic are in
[data-contract.md](data-contract.md).
