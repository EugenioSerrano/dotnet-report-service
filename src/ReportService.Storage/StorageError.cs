namespace ReportService.Storage;

/// <summary>
/// Why a storage operation did not succeed: a stable <paramref name="Code"/>
/// (see <see cref="StorageErrorCodes"/>) and a message for the operator. The
/// message never contains a key, a path, a URL or a credential; the provider's
/// own exception, when there is one, is kept in <paramref name="Inner"/> and
/// never copied into the message.
/// </summary>
/// <param name="Code">A stable code such as <c>storage.already-exists</c>.</param>
/// <param name="Message">A message for the operator, free of keys, paths, URLs and credentials.</param>
/// <param name="Inner">The provider exception that caused the failure, if any.</param>
public sealed record StorageError(string Code, string Message, Exception? Inner = null);
