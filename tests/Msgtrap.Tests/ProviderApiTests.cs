using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Msgtrap.Tests;

/// <summary>
/// Contract tests for the Hubtel, Twilio and mNotify adapters, plus the provider
/// registry. Each test resets settings first because the store is shared by the
/// hosted app.
/// </summary>
public sealed class ProviderApiTests(MsgtrapFactory factory) : IClassFixture<MsgtrapFactory>
{
    private const string TwilioSid = "AC00000000000000000000000000000000";
    private const string TwilioToken = "msgtrap-twilio-token";
    private const string HubtelId = "msgtrap-hubtel-client";
    private const string HubtelSecret = "msgtrap-hubtel-secret";
    private const string MnotifyKey = "msgtrap-mnotify-key";

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task ResetAsync(bool enforceCredentials = false)
    {
        (await _client.PutAsJsonAsync("/api/control/settings", new
        {
            enforceCredentials,
            forceError = "",
            deliverAfterMs = 1500,
            disabledProviders = Array.Empty<string>(),
        }, Ct)).EnsureSuccessStatusCode();

        (await _client.DeleteAsync("/api/control/messages", Ct)).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> NewestMessageAsync()
    {
        var body = await ReadJsonAsync(await _client.GetAsync("/api/control/messages", Ct));
        return body.GetProperty("messages")[0];
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    /* ------------------------------- Hubtel ------------------------------- */

    private async Task<HttpResponseMessage> HubtelSendAsync(
        string clientId = HubtelId, string clientSecret = HubtelSecret,
        string to = "233544919953", string content = "hubtel hello")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/send")
        {
            RequestUri = new Uri("/v1/messages/send", UriKind.Relative),
            Content = JsonContent.Create(new { from = "Msgtrap", to, content }),
        };
        request.Headers.Authorization = Basic(clientId, clientSecret);
        return await _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Hubtel_PostWithBasicAuthAndJson_ReturnsDocumentedShape()
    {
        await ResetAsync();

        var response = await HubtelSendAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.True(Guid.TryParse(body.GetProperty("MessageId").GetString(), out _));
        Assert.Equal("Success", body.GetProperty("Status").GetString());
        Assert.Equal("62002", body.GetProperty("NetworkId").GetString());

        var message = await NewestMessageAsync();
        Assert.Equal("hubtel", message.GetProperty("provider").GetString());
        Assert.Equal("233544919953", message.GetProperty("recipients")[0].GetProperty("number").GetString());
    }

    [Fact]
    public async Task Hubtel_LegacyQueryStringForm_IsAlsoAccepted()
    {
        await ResetAsync();

        var response = await _client.GetAsync(
            $"/v1/messages/send?ClientId={HubtelId}&ClientSecret={HubtelSecret}&From=Msgtrap&To=233544919953&Content=legacy", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Success", (await ReadJsonAsync(response)).GetProperty("Status").GetString());
    }

    [Fact]
    public async Task Hubtel_WithWrongCredentials_ReturnsUnauthorizedWhenEnforced()
    {
        await ResetAsync(enforceCredentials: true);

        try
        {
            var response = await HubtelSendAsync(clientSecret: "nope");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var accepted = await HubtelSendAsync();
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task Hubtel_WithoutContent_ReturnsBadRequest()
    {
        await ResetAsync();

        var response = await HubtelSendAsync(content: " ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BadRequest", (await ReadJsonAsync(response)).GetProperty("Status").GetString());
    }

    /* ------------------------------- Twilio ------------------------------- */

    private Task<HttpResponseMessage> TwilioSendAsync(
        string sid = TwilioSid, string token = TwilioToken,
        string? to = "+233544919953", string? body = "twilio hello",
        string accountSidInPath = TwilioSid)
    {
        var form = new Dictionary<string, string> { ["From"] = "Msgtrap" };
        if (to is not null) form["To"] = to;
        if (body is not null) form["Body"] = body;

        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/2010-04-01/Accounts/{accountSidInPath}/Messages.json")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = Basic(sid, token);
        return _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Twilio_CreateMessage_Returns201WithMessageResource()
    {
        await ResetAsync();

        var response = await TwilioSendAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.StartsWith("SM", body.GetProperty("sid").GetString());
        Assert.Equal("queued", body.GetProperty("status").GetString());
        Assert.Equal("outbound-api", body.GetProperty("direction").GetString());
        Assert.Equal("2010-04-01", body.GetProperty("api_version").GetString());
        Assert.Equal("+233544919953", body.GetProperty("to").GetString());

        var message = await NewestMessageAsync();
        Assert.Equal("twilio", message.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Twilio_WithoutTo_ReturnsDocumentedErrorCode()
    {
        await ResetAsync();

        var response = await TwilioSendAsync(to: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(21604, body.GetProperty("code").GetInt32());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Twilio_WithWrongAuthToken_ReturnsAuthenticationError()
    {
        await ResetAsync(enforceCredentials: true);

        try
        {
            var rejected = await TwilioSendAsync(token: "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal(20003, (await ReadJsonAsync(rejected)).GetProperty("code").GetInt32());

            var accepted = await TwilioSendAsync();
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task Twilio_WithUnknownAccountSidInPath_ReturnsNotFound()
    {
        await ResetAsync(enforceCredentials: true);

        try
        {
            var response = await TwilioSendAsync(accountSidInPath: "AC11111111111111111111111111111111");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await ResetAsync();
        }
    }

    /* ------------------------------- mNotify ------------------------------ */

    private Task<HttpResponseMessage> MnotifySendAsync(
        string key = MnotifyKey,
        object? body = null,
        string path = "/api/sms/quick")
    {
        body ??= new
        {
            recipient = new[] { "0241234567", "0201234567" },
            sender = "mNotify",
            message = "API messaging is fun!",
            is_schedule = false,
            schedule_date = "",
        };
        return _client.PostAsJsonAsync($"{path}?key={key}", body, Ct);
    }

    [Fact]
    public async Task Mnotify_QuickSend_ReturnsDocumentedEnvelope()
    {
        await ResetAsync();

        var response = await MnotifySendAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal("2000", body.GetProperty("code").GetString());
        Assert.Equal("messages sent successfully", body.GetProperty("message").GetString());

        var summary = body.GetProperty("summary");
        Assert.Equal("API QUICK SMS", summary.GetProperty("type").GetString());
        Assert.Equal(2, summary.GetProperty("total_sent").GetInt32());
        Assert.Equal(2, summary.GetProperty("numbers_sent").GetArrayLength());

        var message = await NewestMessageAsync();
        Assert.Equal("mnotify", message.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Mnotify_WithWrongKey_ReturnsUnauthorizedWhenEnforced()
    {
        await ResetAsync(enforceCredentials: true);

        try
        {
            var rejected = await MnotifySendAsync(key: "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal("1001", (await ReadJsonAsync(rejected)).GetProperty("code").GetString());

            var accepted = await MnotifySendAsync();
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task Mnotify_WithoutRecipients_ReturnsValidationError()
    {
        await ResetAsync();

        var response = await MnotifySendAsync(body: new { sender = "mNotify", message = "no recipients" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("1004", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mnotify_Balance_ReportsFakeCredits()
    {
        await ResetAsync();

        var response = await _client.GetAsync($"/api/balance/sms?key={MnotifyKey}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("balance").GetInt32() > 0);
    }

    /* ------------------------------ registry ------------------------------ */

    [Fact]
    public async Task Providers_Endpoint_ListsEveryLoadedAdapterWithCredentials()
    {
        await ResetAsync();

        var body = await ReadJsonAsync(await _client.GetAsync("/api/control/providers", Ct));
        var providers = body.GetProperty("providers");

        var ids = providers.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains("arkesel", ids);
        Assert.Contains("hubtel", ids);
        Assert.Contains("twilio", ids);
        Assert.Contains("mnotify", ids);

        var twilio = providers.EnumerateArray().First(p => p.GetProperty("id").GetString() == "twilio");
        Assert.Equal(TwilioSid, twilio.GetProperty("credentials").GetProperty("account-sid").GetString());
        Assert.True(twilio.GetProperty("enabled").GetBoolean());
        Assert.NotEmpty(twilio.GetProperty("endpoints").EnumerateArray());
    }

    [Fact]
    public async Task DisablingAProvider_MakesItsRoutes404_WithoutAffectingOthers()
    {
        await ResetAsync();

        try
        {
            (await _client.PutAsJsonAsync("/api/control/settings", new
            {
                disabledProviders = new[] { "twilio" },
            }, Ct)).EnsureSuccessStatusCode();

            var twilio = await TwilioSendAsync();
            Assert.Equal(HttpStatusCode.NotFound, twilio.StatusCode);

            // Hubtel still works.
            var hubtel = await HubtelSendAsync();
            Assert.Equal(HttpStatusCode.OK, hubtel.StatusCode);
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task EachProviderMessageIsTaggedWithItsProvider()
    {
        await ResetAsync();

        await HubtelSendAsync();
        await TwilioSendAsync();
        await MnotifySendAsync();

        var messages = (await ReadJsonAsync(await _client.GetAsync("/api/control/messages", Ct)))
            .GetProperty("messages")
            .EnumerateArray()
            .Select(m => m.GetProperty("provider").GetString())
            .ToList();

        Assert.Equal(3, messages.Count);
        Assert.Contains("hubtel", messages);
        Assert.Contains("twilio", messages);
        Assert.Contains("mnotify", messages);
    }

    [Fact]
    public async Task Messages_CanBeFilteredByProvider()
    {
        await ResetAsync();

        await HubtelSendAsync();
        await TwilioSendAsync();

        var filtered = await ReadJsonAsync(
            await _client.GetAsync("/api/control/messages?provider=twilio", Ct));

        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        Assert.Equal("twilio",
            filtered.GetProperty("messages")[0].GetProperty("provider").GetString());
    }
}
