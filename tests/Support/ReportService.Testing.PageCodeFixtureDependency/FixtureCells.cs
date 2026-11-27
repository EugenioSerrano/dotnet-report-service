using System.Globalization;
using System.Text;

namespace ReportService.Testing.PageCodeFixtureDependency;

/// <summary>Draws one row of cells per character of a value: the dependency's whole behaviour.</summary>
public static class FixtureCells
{
    /// <summary>The content for a value in an area.</summary>
    /// <param name="value">The value.</param>
    /// <param name="width">The area's width, in points.</param>
    /// <param name="height">The area's height, in points.</param>
    /// <returns>The content bytes.</returns>
    public static byte[] Draw(string value, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder("q\n0 g\n");
        var cell = width / 16;
        for (var index = 0; index < Math.Min(value.Length, 16); index++)
        {
            var size = (value[index] % 16) + 1;
            builder.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{index * cell:0.####} 0 {cell:0.####} {height * size / 16:0.####} re\n"));
        }

        builder.Append("f\nQ\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
