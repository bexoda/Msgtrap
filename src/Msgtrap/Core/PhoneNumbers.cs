using System.Text.RegularExpressions;

namespace Msgtrap.Core;

/// <summary>
/// Number handling shared by the SMS and OTP endpoints.
/// </summary>
/// <remarks>
/// Arkesel expects international format without a leading <c>+</c>. Validation is
/// intentionally permissive (7-15 digits) because test numbers are usually fake;
/// real carrier coverage rules are not replicated.
/// </remarks>
public static partial class PhoneNumbers
{
    [GeneratedRegex("^[0-9]{7,15}$")]
    private static partial Regex DigitsOnly();

    /// <summary>Strips separators so <c>+233 54 491 9953</c> becomes <c>233544919953</c>.</summary>
    public static string Normalize(string? value) =>
        (value ?? "").Trim()
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("(", "", StringComparison.Ordinal)
            .Replace(")", "", StringComparison.Ordinal)
            .Replace("+", "", StringComparison.Ordinal);

    /// <summary>True when the normalized value is 7-15 digits.</summary>
    public static bool IsValid(string normalized) => DigitsOnly().IsMatch(normalized);
}
