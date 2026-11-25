using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msgtrap.Core;
using Msgtrap.Store;

namespace Msgtrap.Arkesel;

/// <summary>Body of <c>POST /api/otp/generate</c>.</summary>
/// <remarks>Nullable fields so missing values produce Arkesel-style validation errors.</remarks>
public sealed class GenerateOtpRequest
{
    /// <summary>Lifetime in minutes (1-10).</summary>
    [JsonPropertyName("expiry")] public int? Expiry { get; set; }

    /// <summary>Code length (6-15).</summary>
    [JsonPropertyName("length")] public int? Length { get; set; }

    /// <summary><c>sms</c> or <c>voice</c>.</summary>
    [JsonPropertyName("medium")] public string? Medium { get; set; }

    /// <summary>Template; must contain <c>%otp_code%</c>. <c>%expiry%</c> is also substituted.</summary>
    [JsonPropertyName("message")] public string? Message { get; set; }

    /// <summary>Destination number.</summary>
    [JsonPropertyName("number")] public string? Number { get; set; }

    /// <summary>Sender ID (max 11 characters).</summary>
    [JsonPropertyName("sender_id")] public string? SenderId { get; set; }

    /// <summary><c>numeric</c> or <c>alphanumeric</c>.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
}

/// <summary>Body of <c>POST /api/otp/verify</c>.</summary>
public sealed class VerifyOtpRequest
{
    /// <summary>The code the user typed.</summary>
    [JsonPropertyName("code")] public string? Code { get; set; }

    /// <summary>The number the code was issued to.</summary>
    [JsonPropertyName("number")] public string? Number { get; set; }
}

/// <summary>
/// Arkesel's OTP endpoints, so OTP flows can be exercised end to end without
/// real SMS. The generated code lands in the inbox like any other message and is
/// exposed as OTP metadata for the UI.
/// </summary>
/// <remarks>
/// Response codes documented by Arkesel: <c>1000</c> (generated), <c>1001</c>
/// (invalid type), <c>1011</c> (generate failed), <c>1100</c> (verified),
/// <c>1101</c> (code required), <c>1106</c> (verify failed). Arkesel does not
/// publish codes for wrong/expired/used codes, so Msgtrap uses
/// <c>1103</c>/<c>1104</c>/<c>1105</c> in the same numeric style.
/// </remarks>
public sealed class OtpEndpoints(
    MessageStore store,
    OtpStore otps,
    ArkeselEndpoints arkesel,
    ILogger<OtpEndpoints> logger)
{
    /// <summary>Success code for <c>/api/otp/generate</c> (Arkesel).</summary>
    public const string CodeGenerated = "1000";

    /// <summary>Validation failure for <c>/api/otp/generate</c> (Arkesel).</summary>
    public const string CodeGenerateInvalid = "1001";

    /// <summary>Internal failure for <c>/api/otp/generate</c> (Arkesel).</summary>
    public const string CodeGenerateFailed = "1011";

    /// <summary>Code verified (Arkesel).</summary>
    public const string CodeVerified = "1100";

    /// <summary>Missing <c>code</c>/<c>number</c> field (Arkesel documents 1101 for <c>code</c>).</summary>
    public const string CodeVerifyInvalid = "1101";

    /// <summary>Wrong code (Msgtrap-defined).</summary>
    public const string CodeWrong = "1103";

    /// <summary>Expired code (Msgtrap-defined).</summary>
    public const string CodeExpired = "1104";

    /// <summary>Code already used (Msgtrap-defined).</summary>
    public const string CodeAlreadyUsed = "1105";

    /// <summary>Internal failure for <c>/api/otp/verify</c> (Arkesel).</summary>
    public const string CodeVerifyFailed = "1106";

    /// <summary>USSD fallback Arkesel returns alongside a generated code.</summary>
    public const string UssdCode = "*928*01#";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Unambiguous alpha charset: no I/O/0/1 so codes can be read off the screen.
    private const string AlphaCharset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string DigitCharset = "0123456789";

    /// <summary>Maps the OTP routes onto the endpoint builder.</summary>
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/otp/generate", (HttpContext context, CancellationToken cancellationToken) =>
            GenerateAsync(context, cancellationToken));
        app.MapPost("/api/otp/verify", (HttpContext context, CancellationToken cancellationToken) =>
            VerifyAsync(context, cancellationToken));
    }

    private async Task<IResult> GenerateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (arkesel.Authorize(context) is { } unauthorized) return unauthorized;

        var (request, raw) = await ReadAsync<GenerateOtpRequest>(context, cancellationToken);
        if (request is null)
            return OtpError(StatusCodes.Status422UnprocessableEntity, CodeGenerateInvalid, ArkeselEndpoints.MsgMessage);

        if (Validate(request) is { } validation)
            return OtpError(StatusCodes.Status422UnprocessableEntity, CodeGenerateInvalid, validation);

        if (store.Settings.ForceError == "500")
        {
            logger.LogInformation("Simulating {Code} response for /api/otp/generate", CodeGenerateFailed);
            return OtpError(StatusCodes.Status500InternalServerError, CodeGenerateFailed, "Internal error");
        }

        var number = PhoneNumbers.Normalize(request.Number);
        var code = CreateCode(request.Type!, request.Length!.Value);
        var expiryMinutes = request.Expiry!.Value;
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(expiryMinutes);
        var body = RenderTemplate(request.Message!, code, expiryMinutes);

        var otp = new OtpInfo
        {
            Code = code,
            Type = request.Type!,
            Medium = request.Medium!,
            ExpiryMinutes = expiryMinutes,
            ExpiresAt = expiresAt,
        };

        // Goes through the normal Arkesel send path so the OTP shows up in the
        // inbox with segment counts, delivery timeline and raw request body.
        var send = new SendSmsRequest
        {
            Sender = request.SenderId!,
            Message = body,
            Recipients = [number],
        };

        var result = arkesel.Send(send, raw, message =>
        {
            if (otp.Medium == "voice")
            {
                message.DeliveryLog.Add(new DeliveryLogEntry
                {
                    At = now,
                    Status = MessageStatus.Queued,
                    Message = "Voice OTP: simulated call, no audio is played",
                });
            }
        }, otp);

        if (result.MessageId is { } messageId)
        {
            otps.Issue(new OtpRecord
            {
                Number = number,
                Code = code,
                Type = otp.Type,
                Medium = otp.Medium,
                CreatedAt = now,
                ExpiresAt = expiresAt,
                MessageId = messageId,
            });
        }

        logger.LogInformation(
            "Generated OTP {MessageId}: {Type} code via {Medium} to {Number}, expires in {Expiry} min",
            result.MessageId, otp.Type, otp.Medium, number, expiryMinutes);

        return Results.Json(new Dictionary<string, object?>
        {
            ["code"] = CodeGenerated,
            ["ussd_code"] = UssdCode,
            ["message"] = "Successful, OTP is being processed for delivery",
        });
    }

    private async Task<IResult> VerifyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (arkesel.Authorize(context) is { } unauthorized) return unauthorized;

        var (request, _) = await ReadAsync<VerifyOtpRequest>(context, cancellationToken);
        if (request is null)
            return OtpError(StatusCodes.Status422UnprocessableEntity, CodeVerifyInvalid, ArkeselEndpoints.MsgMessage);

        if (string.IsNullOrWhiteSpace(request.Code))
            return OtpError(StatusCodes.Status422UnprocessableEntity, CodeVerifyInvalid, "The code field is required.");
        if (string.IsNullOrWhiteSpace(request.Number))
            return OtpError(StatusCodes.Status422UnprocessableEntity, CodeVerifyInvalid, "The number field is required.");

        if (store.Settings.ForceError == "500")
        {
            logger.LogInformation("Simulating {Code} response for /api/otp/verify", CodeVerifyFailed);
            return OtpError(StatusCodes.Status500InternalServerError, CodeVerifyFailed, "Internal error");
        }

        var number = PhoneNumbers.Normalize(request.Number);
        var now = DateTimeOffset.UtcNow;
        var outcome = otps.Verify(number, request.Code!, now);

        if (outcome == OtpVerifyResult.Success)
        {
            // Reflect verification in the captured message so the UI updates live.
            var record = otps.Get(number);
            if (record is not null)
            {
                store.Update(record.MessageId, message =>
                {
                    if (message.Otp is null) return;
                    message.Otp.Verified = true;
                    message.Otp.VerifiedAt = now;
                    message.DeliveryLog.Add(new DeliveryLogEntry
                    {
                        At = now,
                        Status = "verified",
                        Message = "OTP verified successfully",
                    });
                });
            }

            logger.LogInformation("Verified OTP for {Number}", number);
            return Results.Json(new Dictionary<string, object?>
            {
                ["code"] = CodeVerified,
                ["message"] = "Successful",
            });
        }

        var (status, code, message) = outcome switch
        {
            OtpVerifyResult.UnknownNumber or OtpVerifyResult.Mismatch =>
                (StatusCodes.Status422UnprocessableEntity, CodeWrong, "Invalid code."),
            OtpVerifyResult.Expired =>
                (StatusCodes.Status422UnprocessableEntity, CodeExpired, "Code has expired. Request a new one."),
            _ => (StatusCodes.Status422UnprocessableEntity, CodeAlreadyUsed, "Code has already been used. Request a new one."),
        };

        var attempts = otps.Get(number)?.Attempts ?? 0;
        logger.LogWarning("OTP verification failed for {Number}: {Outcome} ({Attempts} failed attempt(s))",
            number, outcome, attempts);

        return OtpError(status, code, message);
    }

    /// <summary>Returns the Arkesel validation message, or <c>null</c> when valid.</summary>
    public static string? Validate(GenerateOtpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Number)) return "The number field is required.";
        if (!PhoneNumbers.IsValid(PhoneNumbers.Normalize(request.Number)))
            return "The number field must be a valid phone number.";

        if (string.IsNullOrWhiteSpace(request.SenderId)) return "The sender_id field is required.";
        if (request.SenderId.Length > 11) return "The sender_id field must not be greater than 11 characters.";

        if (string.IsNullOrWhiteSpace(request.Message)) return ArkeselEndpoints.MsgMessage;
        if (!request.Message.Contains("%otp_code%", StringComparison.Ordinal))
            return "The message field must contain the %otp_code% placeholder.";

        if (request.Expiry is not { } expiry) return "The expiry field is required.";
        if (expiry is < 1 or > 10) return "The expiry field must be between 1 and 10 minutes.";

        if (request.Length is not { } length) return "The length field is required.";
        if (length is < 6 or > 15) return "The length field must be between 6 and 15 characters.";

        if (string.IsNullOrWhiteSpace(request.Medium)) return "The medium field is required.";
        if (request.Medium is not ("sms" or "voice"))
            return $"The medium you selected [{request.Medium}] is invalid. Expected one of [sms, voice]";

        if (string.IsNullOrWhiteSpace(request.Type)) return "The type field is required.";
        // Arkesel's documented wording, including the offending value.
        if (request.Type is not ("numeric" or "alphanumeric"))
            return $"The type you selected [{request.Type}] is invalid. Expected one of [numeric, alphanumeric]";

        return null;
    }

    /// <summary>Builds a crypto-random code that satisfies the requested type.</summary>
    public static string CreateCode(string type, int length)
    {
        if (type == "numeric")
            return new string([.. Enumerable.Range(0, length).Select(_ => DigitCharset[RandomNumberGenerator.GetInt32(DigitCharset.Length)])]);

        // Alphanumeric must contain both letters and digits, so seed one of each.
        var chars = new char[length];
        chars[0] = AlphaCharset[RandomNumberGenerator.GetInt32(AlphaCharset.Length)];
        chars[1] = DigitCharset[RandomNumberGenerator.GetInt32(DigitCharset.Length)];
        for (var i = 2; i < length; i++)
            chars[i] = AlphaCharset[RandomNumberGenerator.GetInt32(AlphaCharset.Length)];

        // Fisher-Yates so the seeded characters are not always first.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    private static string RenderTemplate(string template, string code, int expiryMinutes) =>
        template
            .Replace("%otp_code%", code, StringComparison.Ordinal)
            .Replace("%expiry%", expiryMinutes.ToString(), StringComparison.Ordinal);

    /// <summary>
    /// Reads and deserializes a JSON body. Returns a <c>null</c> request when the
    /// body is empty or not valid JSON, so callers emit the same validation error.
    /// </summary>
    private static async Task<(T? Request, Dictionary<string, object?> Raw)> ReadAsync<T>(
        HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            var request = JsonSerializer.Deserialize<T>(payload, Json);
            var raw = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload, Json) ?? [];
            return (request, raw);
        }
        catch (JsonException)
        {
            return (null, []);
        }
    }

    /// <summary>Arkesel's OTP error envelope uses <c>code</c>, not <c>status</c>.</summary>
    private static IResult OtpError(int status, string code, string message) =>
        Results.Json(new Dictionary<string, object?> { ["code"] = code, ["message"] = message }, statusCode: status);
}
