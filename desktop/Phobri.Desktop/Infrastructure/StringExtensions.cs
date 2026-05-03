namespace Phobri.Desktop.Infrastructure;

/// <summary>
/// Small utility extensions.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Truncate a string to maxLength, appending "..." if truncated.
    /// </summary>
    public static string Truncate(this string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value ?? string.Empty;
        return value[..maxLength] + "...";
    }
}
