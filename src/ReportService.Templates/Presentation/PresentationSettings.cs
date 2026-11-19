using System.Globalization;

namespace ReportService.Templates;

/// <summary>
/// The culture, the time zone and the clock one block is presented with.
/// <para>
/// One instance is resolved per block and handed to both the render context and the
/// presentation functions, so the values a template can print and the values it formats
/// with can never disagree. <see cref="Clock"/> is the job's acceptance instant, in UTC.
/// </para>
/// </summary>
/// <param name="Culture">The culture dates and numbers are presented in.</param>
/// <param name="TimeZone">The zone an instant is shown in.</param>
/// <param name="Clock">The report's "now", in UTC.</param>
public sealed record PresentationSettings(CultureInfo Culture, TimeZoneInfo TimeZone, DateTimeOffset Clock);

/// <summary>
/// The values a job holds before they are resolved. Each is absent when the job does not
/// carry it, which the resolver refuses rather than filling from a default.
/// </summary>
/// <param name="Culture">The job's culture name, or <see langword="null"/>.</param>
/// <param name="TimeZone">The job's time zone name, or <see langword="null"/>.</param>
/// <param name="Clock">The job's fixed instant, or <see langword="null"/>.</param>
public sealed record PresentationValues(string? Culture, string? TimeZone, DateTimeOffset? Clock);
