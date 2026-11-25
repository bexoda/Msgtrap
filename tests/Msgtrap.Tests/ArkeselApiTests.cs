using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Msgtrap.Tests;

/// <summary>Hosts Msgtrap in-process with a TestServer (no real port is bound).</summary>
public sealed class MsgtrapFactory : WebApplicationFactory<Program> { }

/// <summary>
/// Contract tests for the Arkesel-compatible surface. Each test resets the
/// simulation settings and empties the inbox first, because the store is global
/// to the hosted app.
/// </summary>
public sealed class ArkeselApiTests(MsgtrapFactory factory) : IClassFixture<MsgtrapFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task ResetAsync(int deliverAfterMs = 1500)
    {
        var settings = await _client.PutAsJsonAsync("/api/control/settings", new
        {
            enforceCredentials = false,
            disabledProviders = Array.Empty<string>(),
            forceError = "",
            deliverAfterMs,
            smsBalance = 2003,
            mainBalance = "GHS 20.99",
        }, Ct);
        settings.EnsureSuccessStatusCode();

        (await _client.DeleteAsync("/api/control/messages", Ct)).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> GetMessagesAsync() =>
        await ReadJsonAsync(await _client.GetAsync("/api/control/messages", Ct));

    private Task<HttpResponseMessage> SendAsync(object body, string apiKey = "msgtrap-dev-key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/sms/send")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        return _client.SendAsync(request, Ct);
    }

    private async Task<JsonElement> SendForJsonAsync(object body) =>
        await ReadJsonAsync(await SendAsync(body));

    private Task<HttpResponseMessage> SetSettingsAsync(object partialUpdate) =>
        _client.PutAsJsonAsync("/api/control/settings", partialUpdate, Ct);

    [Fact]
    public async Task Send_WithValidRecipients_ReturnsArkeselSuccessEnvelope()
    {
        await ResetAsync();

        var body = await SendForJsonAsync(new
        {
            sender = "Arkesel",
            message = "Hello from Msgtrap",
            recipients = new[] { "233544919953", "+233 20 123 4567", "not-a-number" },
        });

        Assert.Equal("success", body.GetProperty("status").GetString());

        var data = body.GetProperty("data");
        Assert.Equal(3, data.GetArrayLength());
        Assert.Equal("233544919953", data[0].GetProperty("recipient").GetString());
        Assert.Equal("233201234567", data[1].GetProperty("recipient").GetString());
        Assert.True(Guid.TryParse(data[0].GetProperty("id").GetString(), out _));
        Assert.Equal("not-a-number", data[2].GetProperty("invalid numbers")[0].GetString());
    }

    [Fact]
    public async Task Send_WithoutMessage_ReturnsArkeselValidationError()
    {
        await ResetAsync();

        var response = await SendAsync(new { sender = "Arkesel", recipients = new[] { "233544919953" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("error", body.GetProperty("status").GetString());
        Assert.Equal("The message field is required.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Send_WithoutRecipients_ReturnsArkeselValidationError()
    {
        await ResetAsync();

        var response = await SendAsync(new { sender = "Arkesel", message = "hi", recipients = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("The recipients field is required.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Send_WithSenderLongerThanElevenCharacters_ReturnsArkeselValidationError()
    {
        await ResetAsync();

        var response = await SendAsync(new
        {
            sender = "A very long sender id",
            message = "hi",
            recipients = new[] { "233544919953" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Send_WithBrokenJson_ReturnsUnprocessableEntity()
    {
        await ResetAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/sms/send")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        };

        var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Send_WhenCredentialEnforcementIsOn_RejectsWrongKeyAndAcceptsConfiguredKey()
    {
        await ResetAsync();
        (await SetSettingsAsync(new { enforceCredentials = true })).EnsureSuccessStatusCode();

        try
        {
            var payload = new { sender = "Arkesel", message = "hi", recipients = new[] { "233544919953" } };

            var rejected = await SendAsync(payload, apiKey: "wrong-key");
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.Equal("Invalid API Key", (await ReadJsonAsync(rejected)).GetProperty("message").GetString());

            var accepted = await SendAsync(payload, apiKey: "msgtrap-dev-key");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Theory]
    [InlineData("402", HttpStatusCode.PaymentRequired, "Insufficient balance or invalid coverage!")]
    [InlineData("403", HttpStatusCode.Forbidden, "Inactive SMS Gateway!")]
    [InlineData("500", HttpStatusCode.InternalServerError, "SMS request failed!")]
    public async Task Send_WhenAnErrorIsForced_ReturnsThatArkeselError(
        string forceError, HttpStatusCode expectedStatus, string expectedMessage)
    {
        await ResetAsync();
        (await SetSettingsAsync(new { forceError })).EnsureSuccessStatusCode();

        try
        {
            var response = await SendAsync(new
            {
                sender = "Arkesel",
                message = "hi",
                recipients = new[] { "233544919953" },
            });

            Assert.Equal(expectedStatus, response.StatusCode);
            var body = await ReadJsonAsync(response);
            Assert.Equal("error", body.GetProperty("status").GetString());
            Assert.Equal(expectedMessage, body.GetProperty("message").GetString());
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task Balance_ReportsTheConfiguredFakeCredits()
    {
        await ResetAsync();
        (await SetSettingsAsync(new { smsBalance = 5000, mainBalance = "GHS 99.50" })).EnsureSuccessStatusCode();

        try
        {
            var response = await _client.GetAsync("/api/v2/clients/balance-details", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var data = (await ReadJsonAsync(response)).GetProperty("data");
            Assert.Equal("5000", data.GetProperty("sms_balance").GetString());
            Assert.Equal("GHS 99.50", data.GetProperty("main_balance").GetString());
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task Message_MovesFromQueuedToDelivered()
    {
        await ResetAsync(deliverAfterMs: 0);

        var response = await SendAsync(new
        {
            sender = "Arkesel",
            message = "deliver me",
            recipients = new[] { "233544919953" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("delivered", await WaitForStatusAsync("delivered"));
    }

    [Fact]
    public async Task ScheduledSend_ReturnsMessageEnvelopeAndIsNotDelivered()
    {
        await ResetAsync();

        var body = await SendForJsonAsync(new
        {
            sender = "Arkesel",
            message = "later",
            recipients = new[] { "233544919953" },
            scheduled_date = "2021-04-09 07:30 AM",
        });

        Assert.Equal("SMS request sent successfully!", body.GetProperty("message").GetString());

        var messages = await GetMessagesAsync();
        Assert.Equal("scheduled", messages.GetProperty("messages")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ui_IsServedFromEmbeddedResources()
    {
        var index = await _client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("Msgtrap", await index.Content.ReadAsStringAsync(Ct));

        var script = await _client.GetAsync("/app.js", Ct);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.NotEmpty(await script.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/styles.css", "text/css")]
    [InlineData("/vendor/base.css", "text/css")]
    [InlineData("/vendor/base.js", "text/javascript")]
    public async Task Ui_AssetsAreServedFromEmbeddedResources(string path, string contentType)
    {
        var response = await _client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Ui_DoesNotAdvertiseItsStack()
    {
        var response = await _client.GetAsync("/", Ct);

        Assert.False(response.Headers.Contains("Server"));

        var html = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("bootstrap", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Control_DeleteRemovesASingleMessage()
    {
        await ResetAsync();
        await SendAsync(new { sender = "A", message = "one", recipients = new[] { "233544919953" } });

        var list = await GetMessagesAsync();
        var id = list.GetProperty("messages")[0].GetProperty("id").GetString();

        var deleted = await _client.DeleteAsync($"/api/control/messages/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(0, (await GetMessagesAsync()).GetProperty("total").GetInt32());
    }

    private async Task<string> WaitForStatusAsync(string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? status = null;

        while (DateTime.UtcNow < deadline)
        {
            var messages = (await GetMessagesAsync()).GetProperty("messages");
            if (messages.GetArrayLength() > 0)
            {
                status = messages[0].GetProperty("status").GetString();
                if (status == expected) return status;
            }
            await Task.Delay(50, Ct);
        }

        return status ?? "(no message)";
    }
}
