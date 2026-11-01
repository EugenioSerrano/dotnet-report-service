# Upload verification

Verifying an upload is the step that turns received bytes into an immutable
snapshot — or ends the upload rejected with a stable reason. It runs in the
orchestration worker, never inside an HTTP request, and it writes nothing until
the whole content has been read and measured.

## What one verification does

1. It loads the upload and refuses to act unless the upload is waiting for
   verification. An upload that is already ready reports the snapshot that
   exists; one that ended rejected reports the reason recorded on it; any other
   state is left untouched.
2. It refuses before reading anything when the body ended early, when the
   declared encoding is one the service does not decode, or when the stored size
   is past the stored-size limit.
3. It reads the stored object **once**, forward only, and never seeks. The same
   pull of the storage port:

   - counts and hashes the stored bytes, and keeps their last eight bytes;
   - hands the blocks to the decompressor when the content is compressed, which
     counts and hashes the content and checksums it.

   The storage port writes a copy of exactly those bytes under a key that belongs
   to this attempt, so the object recorded in a snapshot is the object that was
   measured, not a second read of something that may have changed.
4. It decides the outcome only after both sides of that read have finished:

   | What happened | Outcome |
   |---------------|---------|
   | The stored object is no longer the size or hash recorded when it arrived, or it is gone | `DATA_INVALID` / `modified` |
   | The decompressor refuses the content it was given | `DATA_INVALID` / `malformed` |
   | The compressed object is shorter than twenty bytes, or its own trailer does not agree with the content that was read | `DATA_INVALID` / `truncated` |
   | The content decompresses past the decompressed-size limit | `QUOTA_EXCEEDED` / `input-bytes` |
   | The declared hash differs from the content's hash | `DATA_INVALID` / `sha256-mismatch` |
   | None of the above | `ready`, with a snapshot record |

5. It commits the outcome with a conditional update: a snapshot record plus the
   move to ready in one transaction, or the move to rejected. An upload that
   stopped waiting in the meantime keeps whatever the other writer left, and this
   attempt's object is removed. The upload's staged object is removed once the
   upload has ended.

## Compressed content: exactly one unit of content

`Content-Encoding: gzip` declares **exactly one** gzip member — one complete unit
of compressed content. A body that is a concatenation of members, or a
compressed body of zero bytes, is refused with `DATA_INVALID` / `truncated`. The
one exception is a chain whose earlier members hold no content at all: the last
member's trailer then attests everything that was read, and the content that is
fixed is exactly that member's own content.

A compressed body whose content is intact but whose own trailer is missing or
cut is refused the same way, **even when the HTTP transfer completed and the
declared hash matches**: the object does not end with a trailer that agrees with
what was measured, so it is not one complete unit of content.

The completeness decision is a comparison of the object's last eight bytes with
what was measured over the content — two 32-bit values, one of them the content's
length modulo 2^32. It is a very strong check, not a mathematical proof: a
producer would have to craft a tail equal to the checksum and length of exactly
the content that is fixed, which grants nothing it did not already have.

## Limits and settings

| Setting | Meaning | Shipped value |
|---------|---------|---------------|
| `Uploads__MaxStoredBytes` | The most stored bytes one upload may have. Enforced while the body arrives and again before verification reads anything. Required, greater than zero. | `4294967296` (4 GiB) |
| `Uploads__MaxUncompressedBytes` | The most content bytes a body may decompress to. Counted as the bytes leave the decompressor, so content that expands far past its stored size is stopped at the limit. Required, greater than zero. | `4294967296` (4 GiB) |

A value that is missing or outside its rule stops the worker at start, naming the
setting and the rule and never the value.

## Instruments

The worker records, on the product meter:

| Instrument | Kind | Tags |
|------------|------|------|
| `reportservice.upload.outcomes` | counter | `outcome`, `code`, `stage` |
| `reportservice.upload.verification.duration` | histogram, seconds | `outcome` |

Every tag is a value from a fixed set. No identifier, key or size is ever a tag.

## Memory

The stored side may run at most one megabyte ahead of the decompressor, and
resumes when it has caught up to half a megabyte, so verification of a body
larger than the process's memory is bounded by that window and by the copy
buffer — never by the body's size.
