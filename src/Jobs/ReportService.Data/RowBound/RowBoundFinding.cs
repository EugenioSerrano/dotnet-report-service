namespace ReportService.Data;

/// <summary>
/// A string value of a detail record that the row bound refuses, as the scan names it: the
/// field's JSON Pointer relative to the record and its decoded length in bytes. The pointer is
/// escaped by the pointer grammar (<c>~0</c> for a tilde, <c>~1</c> for a slash), so a member
/// name holding either is still one segment. It carries a position and a size, never a value.
/// </summary>
/// <param name="JsonPointer">The field's path relative to the record, as a JSON Pointer.</param>
/// <param name="DecodedBytes">The field's length once decoded, in bytes of UTF-8.</param>
public readonly record struct RowBoundFinding(string JsonPointer, long DecodedBytes);
