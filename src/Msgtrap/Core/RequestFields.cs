using Microsoft.Extensions.Primitives;

namespace Msgtrap.Core;

/// <summary>
/// Small helpers for providers that accept query strings, form bodies or both.
/// Kept here so a new adapter does not re-implement case-insensitive lookups.
/// </summary>
public static class RequestFields
{
    /// <summary>
    /// Merges query string and form body into one case-insensitive dictionary.
    /// Form values win, because that is what a POST actually carries.
    /// </summary>
    public static async Task<Dictionary<string, string>> QueryOrFormAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in context.Request.Query)
            fields[key] = value.ToString();

        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            foreach (var (key, value) in form)
                fields[key] = value.ToString();
        }

        return fields;
    }

    /// <summary>Returns the first non-empty value among <paramref name="keys"/>.</summary>
    public static string? Pick(this IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    /// <summary>Reads a single header, tolerating absence.</summary>
    public static string? Header(this HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out StringValues values) ? values.FirstOrDefault() : null;

    /// <summary>Decodes HTTP Basic credentials into a user/password pair.</summary>
    public static (string User, string Password)? BasicAuth(this HttpContext context)
    {
        var header = context.Header("Authorization");
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var separator = decoded.IndexOf(':');
            return separator < 0
                ? (decoded, "")
                : (decoded[..separator], decoded[(separator + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
