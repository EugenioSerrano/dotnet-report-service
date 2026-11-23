using ReportService.Uploads;

namespace ReportService.Api.Uploads;

/// <summary>
/// What a consumer asks for when it opens an upload session: the shape of the
/// data it will send and the way it will send it, as the raw strings it declared.
/// Whether those strings name something this increment offers is decided after
/// the document is read, so a well-formed request for an unsupported thing stays
/// distinguishable from a malformed one.
/// </summary>
/// <param name="Profile">The declared data profile, as received.</param>
/// <param name="Mode">The declared upload mode, as received.</param>
public sealed record CreateUploadRequest(string Profile, string Mode);
