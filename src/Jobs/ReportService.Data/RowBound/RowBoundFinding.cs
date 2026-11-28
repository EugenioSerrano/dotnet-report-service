namespace ReportService.Data;

/// <summary>
/// A string value of a detail record that the row bound refuses, as the scan names it: the
/// field's JSON Pointer relative to the record, its decoded length in bytes and the bound that
/// applied to it. The pointer is
/// escaped by the pointer grammar (<c>~0</c> for a tilde, <c>~1</c> for a slash), so a member
/// name holding either is still one segment. It carries a position and a size, never a value.
/// </summary>
/// <param name="JsonPointer">The field's path relative to the record, as a JSON Pointer.</param>
/// <param name="DecodedBytes">The field's length once decoded, in bytes of UTF-8.</param>
/// <param name="BoundBytes">The bound the value was measured against: the ordinary-string bound, or the image data URI bound when the value is one.</param>
public readonly record struct RowBoundFinding(string JsonPointer, long DecodedBytes, long BoundBytes);
