using Msgtrap.Store;
using Xunit;

namespace Msgtrap.Tests;

/// <summary>
/// Expiry, single-use and resend semantics. Tested against the store directly
/// because waiting out a real 1-10 minute OTP lifetime would be untenable.
/// </summary>
public sealed class OtpStoreTests
{
    private static OtpRecord Record(TimeSpan lifetime, string code = "123456") => new()
    {
        Number = "233544919953",
        Code = code,
        Type = "numeric",
        Medium = "sms",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime),
        MessageId = "message-1",
    };

    [Fact]
    public void Verify_WithMatchingCodeAndTimeRemaining_Succeeds()
    {
        var store = new OtpStore();
        var record = store.Issue(Record(TimeSpan.FromMinutes(5)));

        Assert.Equal(OtpVerifyResult.Success, store.Verify(record.Number, "123456", DateTimeOffset.UtcNow));
        Assert.True(record.Verified);
    }

    [Fact]
    public void Verify_AfterExpiry_ReportsExpiredEvenWithCorrectCode()
    {
        var store = new OtpStore();
        var record = store.Issue(Record(TimeSpan.FromMilliseconds(1)));
        var later = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.Equal(OtpVerifyResult.Expired, store.Verify(record.Number, record.Code, later));
        Assert.False(record.Verified);
    }

    [Fact]
    public void Verify_AfterSuccess_ReportsAlreadyUsed()
    {
        var store = new OtpStore();
        var record = store.Issue(Record(TimeSpan.FromMinutes(5)));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(OtpVerifyResult.Success, store.Verify(record.Number, record.Code, now));
        Assert.Equal(OtpVerifyResult.AlreadyUsed, store.Verify(record.Number, record.Code, now));
    }

    [Fact]
    public void Verify_WithWrongCode_CountsAnAttemptAndStaysUsable()
    {
        var store = new OtpStore();
        var record = store.Issue(Record(TimeSpan.FromMinutes(5)));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(OtpVerifyResult.Mismatch, store.Verify(record.Number, "999999", now));
        Assert.Equal(OtpVerifyResult.Mismatch, store.Verify(record.Number, "999998", now));
        Assert.Equal(2, record.Attempts);

        // A wrong guess must not burn the real code.
        Assert.Equal(OtpVerifyResult.Success, store.Verify(record.Number, record.Code, now));
    }

    [Fact]
    public void Verify_ForUnknownNumber_ReportsUnknownNumber()
    {
        var store = new OtpStore();

        Assert.Equal(OtpVerifyResult.UnknownNumber,
            store.Verify("233200000001", "123456", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Issue_ReplacesThePreviousCodeForTheSameNumber()
    {
        var store = new OtpStore();
        store.Issue(Record(TimeSpan.FromMinutes(5), "111111"));
        var replacement = store.Issue(Record(TimeSpan.FromMinutes(5), "222222"));

        Assert.Equal("222222", store.Get(replacement.Number)!.Code);
        Assert.Equal(OtpVerifyResult.Mismatch,
            store.Verify(replacement.Number, "111111", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Verify_ComparesCodesCaseInsensitively()
    {
        var store = new OtpStore();
        var record = store.Issue(Record(TimeSpan.FromMinutes(5), "A7K2Q9"));

        Assert.Equal(OtpVerifyResult.Success, store.Verify(record.Number, "a7k2q9", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Clear_DropsEveryOutstandingCode()
    {
        var store = new OtpStore();
        var record = store.Issue(Record(TimeSpan.FromMinutes(5)));
        store.Clear();

        Assert.Null(store.Get(record.Number));
        Assert.Equal(OtpVerifyResult.UnknownNumber,
            store.Verify(record.Number, record.Code, DateTimeOffset.UtcNow));
    }
}
