using System.Globalization;

// A deterministic child that writes a requested volume to standard error first and
// then to standard output, then exits with a requested code. Writing standard error
// first, without draining standard output, is what deadlocks a reader that drains
// standard output to completion before it starts reading standard error: once the
// standard-error pipe buffer fills, this child blocks on the write while that reader
// stays blocked on standard output this child has not produced yet.

var stdErrBytes = 0;
var stdOutBytes = 0;
var exitCode = 0;

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--stderr-bytes":
            stdErrBytes = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--stdout-bytes":
            stdOutBytes = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--exit-code":
            exitCode = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        default:
            break;
    }
}

WriteFiller(Console.OpenStandardError(), (byte)'E', stdErrBytes);
WriteFiller(Console.OpenStandardOutput(), (byte)'O', stdOutBytes);
return exitCode;

static void WriteFiller(Stream stream, byte value, int count)
{
    using (stream)
    {
        var buffer = new byte[Math.Min(64 * 1024, Math.Max(count, 1))];
        Array.Fill(buffer, value);

        var remaining = count;
        while (remaining > 0)
        {
            var take = Math.Min(remaining, buffer.Length);
            stream.Write(buffer.AsSpan(0, take));
            remaining -= take;
        }

        stream.Flush();
    }
}
