namespace Msgtrap.Store;

/// <summary>Outcome of an OTP verification attempt.</summary>
public enum OtpVerifyResult
{
    /// <summary>Code matched, was still valid and had not been used.</summary>
    Success,

    /// <summary>No OTP has ever been generated for that number.</summary>
    UnknownNumber,

    /// <summary>Code did not match the one issued for that number.</summary>
    Mismatch,

    /// <summary>Correct code, but its lifetime elapsed.</summary>
    Expired,

    /// <summary>Correct code, but it was already verified once.</summary>
    AlreadyUsed,
}

/// <summary>
/// A single outstanding OTP. Kept separately from the message store because
/// verification is keyed by number, exactly like Arkesel's
/// <c>/api/otp/verify</c> which only accepts <c>code</c> and <c>number</c>.
/// </summary>
public sealed class OtpRecord
{
    /// <summary>Normalized destination number.</summary>
    public string Number { get; init; } = "";

    /// <summary>The generated code.</summary>
    public string Code { get; init; } = "";

    /// <summary><c>numeric</c> or <c>alphanumeric</c>.</summary>
    public string Type { get; init; } = "";

    /// <summary><c>sms</c> or <c>voice</c>.</summary>
    public string Medium { get; init; } = "";

    /// <summary>When the code was issued (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the code stops being accepted (UTC).</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Id of the captured message carrying the code.</summary>
    public string MessageId { get; init; } = "";

    /// <summary>True once verified; a verified code cannot be reused.</summary>
    public bool Verified { get; set; }

    /// <summary>Failed attempts so far, surfaced in the UI.</summary>
    public int Attempts { get; set; }
}

/// <summary>
/// In-memory OTP registry with latest-code-wins semantics: issuing a new code for
/// a number invalidates the previous one, which is what carriers and most OTP
/// providers do on resend.
/// </summary>
public sealed class OtpStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OtpRecord> _byNumber = [];

    /// <summary>Registers (or replaces) the outstanding OTP for a number.</summary>
    public OtpRecord Issue(OtpRecord record)
    {
        lock (_gate) _byNumber[record.Number] = record;
        return record;
    }

    /// <summary>Returns the current OTP for a number, or <c>null</c>.</summary>
    public OtpRecord? Get(string number)
    {
        lock (_gate) return _byNumber.GetValueOrDefault(number);
    }

    /// <summary>Checks a code and, on success, marks it used.</summary>
    /// <param name="number">Normalized number the code was issued to.</param>
    /// <param name="code">Code supplied by the caller.</param>
    /// <param name="now">Current time; injected so expiry is testable.</param>
    public OtpVerifyResult Verify(string number, string code, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_byNumber.TryGetValue(number, out var record)) return OtpVerifyResult.UnknownNumber;

            // Alphanumeric codes are generated upper-case; accept any casing.
            if (!string.Equals(record.Code, code.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                record.Attempts++;
                return OtpVerifyResult.Mismatch;
            }
            if (record.Verified) return OtpVerifyResult.AlreadyUsed;
            if (now > record.ExpiresAt) return OtpVerifyResult.Expired;

            record.Verified = true;
            return OtpVerifyResult.Success;
        }
    }

    /// <summary>Drops every outstanding OTP. Used by tests and by "clear all".</summary>
    public void Clear()
    {
        lock (_gate) _byNumber.Clear();
    }
}
