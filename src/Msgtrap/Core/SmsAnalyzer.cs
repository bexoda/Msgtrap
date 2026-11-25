namespace Msgtrap.Core;

public readonly record struct SmsAnalysis(string Encoding, int Chars, int Segments);

/// <summary>
/// Counts billed characters and segments the way carriers do: GSM-7 at
/// 160/153 characters, UCS-2 at 70/67 UTF-16 code units.
/// </summary>
public static class SmsAnalyzer
{
    public const string EncodingGsm7 = "GSM-7";
    public const string EncodingUcs2 = "UCS-2";

    private const string Gsm7Basic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    // Extension characters occupy two septets (ESC + code).
    private const string Gsm7Extended = "^{}\\[~]|€";

    public static SmsAnalysis Analyze(string body)
    {
        var septets = 0;
        foreach (var rune in body.EnumerateRunes())
        {
            if (Gsm7Basic.Contains(rune.ToString(), StringComparison.Ordinal))
            {
                septets++;
            }
            else if (Gsm7Extended.Contains(rune.ToString(), StringComparison.Ordinal))
            {
                septets += 2;
            }
            else
            {
                var units = System.Text.Encoding.Unicode.GetByteCount(body) / 2;
                return new SmsAnalysis(EncodingUcs2, units, Parts(units, 70, 67));
            }
        }
        return new SmsAnalysis(EncodingGsm7, septets, Parts(septets, 160, 153));
    }

    private static int Parts(int count, int single, int multipart) =>
        count <= single ? 1 : (count + multipart - 1) / multipart;
}
