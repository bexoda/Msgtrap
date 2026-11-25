using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Msgtrap.Tests;

/// <summary>
/// Contract tests for Arkesel's OTP endpoints. The generated code is read back
/// from the captured message body, which is exactly how a developer reads it in
/// the UI.
/// </summary>
public sealed class OtpApiTests(MsgtrapFactory factory) : IClassFixture<MsgtrapFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task ResetAsync()
    {
        (await _client.PutAsJsonAsync("/api/control/settings", new
        {
            enforceCredentials = false,
            disabledProviders = Array.Empty<string>(),
            forceError = "",
            deliverAfterMs = 1500,
        }, Ct)).EnsureSuccessStatusCode();

        (await _client.DeleteAsync("/api/control/messages", Ct)).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private Task<HttpResponseMessage> GenerateAsync(object body, string apiKey = "msgtrap-dev-key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/otp/generate") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        return _client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> VerifyAsync(object body, string apiKey = "msgtrap-dev-key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/otp/verify") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        return _client.SendAsync(request, Ct);
    }

    private static object GenerateBody(string number = "233544919953", string type = "numeric",
        int length = 6, int expiry = 5, string medium = "sms", string senderId = "Arkesel") => new
        {
            expiry,
            length,
            medium,
            message = "This is OTP from Arkesel, %otp_code%. Code expires in %expiry% minutes",
            number,
            sender_id = senderId,
            type,
        };

    /// <summary>Reads the newest captured message, which is the OTP SMS.</summary>
    private async Task<JsonElement> NewestMessageAsync()
    {
        var body = await ReadJsonAsync(await _client.GetAsync("/api/control/messages", Ct));
        return body.GetProperty("messages")[0];
    }

    private static string CodeFromMessage(JsonElement message) =>
        message.GetProperty("otp").GetProperty("code").GetString()!;

    [Fact]
    public async Task Generate_ReturnsDocumentedEnvelopeAndCapturesTheCodeAsAMessage()
    {
        await ResetAsync();

        var response = await GenerateAsync(GenerateBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.Equal("1000", body.GetProperty("code").GetString());
        Assert.Equal("*928*01#", body.GetProperty("ussd_code").GetString());
        Assert.Equal("Successful, OTP is being processed for delivery", body.GetProperty("message").GetString());

        var message = await NewestMessageAsync();
        var code = CodeFromMessage(message);

        Assert.Matches(new Regex("^[0-9]{6}$"), code);
        Assert.Contains(code, message.GetProperty("body").GetString()!);
        Assert.Contains("5 minutes", message.GetProperty("body").GetString()!);
        Assert.Equal("numeric", message.GetProperty("otp").GetProperty("type").GetString());
        Assert.Equal("sms", message.GetProperty("otp").GetProperty("medium").GetString());
        Assert.False(message.GetProperty("otp").GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task Generate_AlphanumericCodeContainsLettersAndDigits()
    {
        await ResetAsync();

        (await GenerateAsync(GenerateBody(type: "alphanumeric", length: 8))).EnsureSuccessStatusCode();

        var code = CodeFromMessage(await NewestMessageAsync());
        Assert.Equal(8, code.Length);
        Assert.Matches(new Regex("[A-Z]"), code);
        Assert.Matches(new Regex("[0-9]"), code);
        Assert.Matches(new Regex("^[A-Z0-9]+$"), code);
    }

    [Fact]
    public async Task Generate_WithInvalidType_ReturnsArkeselWording()
    {
        await ResetAsync();

        var response = await GenerateAsync(GenerateBody(type: "numeri"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("1001", body.GetProperty("code").GetString());
        Assert.Equal(
            "The type you selected [numeri] is invalid. Expected one of [numeric, alphanumeric]",
            body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("number", "The number field is required.")]
    [InlineData("message", "The message field is required.")]
    [InlineData("placeholder", "The message field must contain the %otp_code% placeholder.")]
    [InlineData("sender", "The sender_id field is required.")]
    [InlineData("sender_long", "The sender_id field must not be greater than 11 characters.")]
    [InlineData("expiry", "The expiry field must be between 1 and 10 minutes.")]
    [InlineData("length", "The length field must be between 6 and 15 characters.")]
    [InlineData("medium", "The medium you selected [fax] is invalid. Expected one of [sms, voice]")]
    public async Task Generate_WithInvalidField_ReturnsValidationError(string scenario, string expectedMessage)
    {
        await ResetAsync();

        var body = new Dictionary<string, object?>
        {
            ["expiry"] = 5,
            ["length"] = 6,
            ["medium"] = "sms",
            ["message"] = "%otp_code%",
            ["number"] = "233544919953",
            ["sender_id"] = "Arkesel",
            ["type"] = "numeric",
        };
        switch (scenario)
        {
            case "number": body.Remove("number"); break;
            case "message": body.Remove("message"); break;
            case "placeholder": body["message"] = "no slot here"; break;
            case "sender": body.Remove("sender_id"); break;
            case "sender_long": body["sender_id"] = "A very long sender"; break;
            case "expiry": body["expiry"] = 45; break;
            case "length": body["length"] = 4; break;
            case "medium": body["medium"] = "fax"; break;
        }

        var response = await GenerateAsync(body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("1001", json.GetProperty("code").GetString());
        Assert.Equal(expectedMessage, json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Verify_WithCorrectCode_ReturnsSuccessAndMarksMessageVerified()
    {
        await ResetAsync();
        (await GenerateAsync(GenerateBody())).EnsureSuccessStatusCode();
        var code = CodeFromMessage(await NewestMessageAsync());

        var response = await VerifyAsync(new { code, number = "233544919953" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("1100", body.GetProperty("code").GetString());
        Assert.Equal("Successful", body.GetProperty("message").GetString());

        var message = await NewestMessageAsync();
        Assert.True(message.GetProperty("otp").GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task Verify_AcceptsNormalizedNumberAndAnyCasing()
    {
        await ResetAsync();
        (await GenerateAsync(GenerateBody(type: "alphanumeric", length: 6))).EnsureSuccessStatusCode();
        var code = CodeFromMessage(await NewestMessageAsync());

        var response = await VerifyAsync(new { code = code.ToLowerInvariant(), number = "+233 54 491 9953" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Verify_WithWrongCode_ReturnsInvalidCode()
    {
        await ResetAsync();
        (await GenerateAsync(GenerateBody())).EnsureSuccessStatusCode();

        var response = await VerifyAsync(new { code = "000000", number = "233544919953" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("1103", body.GetProperty("code").GetString());
        Assert.Equal("Invalid code.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Verify_WithUnknownNumber_ReturnsInvalidCode()
    {
        await ResetAsync();

        var response = await VerifyAsync(new { code = "123456", number = "233200000001" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("1103", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Verify_WithoutCode_ReturnsDocumentedValidationError()
    {
        await ResetAsync();

        var response = await VerifyAsync(new { number = "233544919953" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("1101", body.GetProperty("code").GetString());
        Assert.Equal("The code field is required.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Verify_WithoutNumber_ReturnsValidationError()
    {
        await ResetAsync();

        var response = await VerifyAsync(new { code = "123456" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("The number field is required.", (await ReadJsonAsync(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Verify_TwiceWithSameCode_IsRejectedAsAlreadyUsed()
    {
        await ResetAsync();
        (await GenerateAsync(GenerateBody())).EnsureSuccessStatusCode();
        var code = CodeFromMessage(await NewestMessageAsync());

        (await VerifyAsync(new { code, number = "233544919953" })).EnsureSuccessStatusCode();

        var second = await VerifyAsync(new { code, number = "233544919953" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("1105", (await ReadJsonAsync(second)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Generate_AgainForSameNumber_ReplacesThePreviousCode()
    {
        await ResetAsync();
        (await GenerateAsync(GenerateBody())).EnsureSuccessStatusCode();
        var first = CodeFromMessage(await NewestMessageAsync());

        (await GenerateAsync(GenerateBody(length: 8))).EnsureSuccessStatusCode();
        var second = CodeFromMessage(await NewestMessageAsync());

        Assert.NotEqual(first, second);

        var response = await VerifyAsync(new { code = first, number = "233544919953" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("1103", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Generate_WhenServerErrorIsForced_ReturnsOtpFailureCode()
    {
        await ResetAsync();
        (await _client.PutAsJsonAsync("/api/control/settings", new { forceError = "500" }, Ct))
            .EnsureSuccessStatusCode();

        try
        {
            var response = await GenerateAsync(GenerateBody());

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await ReadJsonAsync(response);
            Assert.Equal("1011", body.GetProperty("code").GetString());
            Assert.Equal("Internal error", body.GetProperty("message").GetString());
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task Otp_EndpointsHonourCredentialEnforcement()
    {
        await ResetAsync();
        (await _client.PutAsJsonAsync("/api/control/settings", new { enforceCredentials = true }, Ct))
            .EnsureSuccessStatusCode();

        try
        {
            var rejected = await GenerateAsync(GenerateBody(), apiKey: "wrong");
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

            var accepted = await GenerateAsync(GenerateBody(), apiKey: "msgtrap-dev-key");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task VoiceMedium_IsRecordedAndStillDeliversTheCode()
    {
        await ResetAsync();

        (await GenerateAsync(GenerateBody(medium: "voice"))).EnsureSuccessStatusCode();

        var message = await NewestMessageAsync();
        Assert.Equal("voice", message.GetProperty("otp").GetProperty("medium").GetString());

        var log = message.GetProperty("deliveryLog").EnumerateArray()
            .Select(entry => entry.GetProperty("message").GetString())
            .ToList();
        Assert.Contains(log, entry => entry!.Contains("Voice OTP", StringComparison.Ordinal));

        var code = CodeFromMessage(message);
        (await VerifyAsync(new { code, number = "233544919953" })).EnsureSuccessStatusCode();
    }
}
