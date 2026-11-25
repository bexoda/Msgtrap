using Msgtrap.Core;
using Xunit;

namespace Msgtrap.Tests;

/// <summary>Segment/encoding rules must match what carriers bill.</summary>
public sealed class SmsAnalyzerTests
{
    [Fact]
    public void Gsm7Text_UsesOneHundredSixtyCharactersPerPart()
    {
        var single = SmsAnalyzer.Analyze(new string('a', 160));
        Assert.Equal(SmsAnalyzer.EncodingGsm7, single.Encoding);
        Assert.Equal(160, single.Chars);
        Assert.Equal(1, single.Segments);

        var multipart = SmsAnalyzer.Analyze(new string('a', 161));
        Assert.Equal(2, multipart.Segments);
    }

    [Fact]
    public void ExtendedGsm7Characters_CountAsTwoSeptets()
    {
        // euro is ESC + code: two septets each, so 80 of them hit the 160 limit exactly.
        var analysis = SmsAnalyzer.Analyze(new string('€', 80));
        Assert.Equal(SmsAnalyzer.EncodingGsm7, analysis.Encoding);
        Assert.Equal(160, analysis.Chars);
        Assert.Equal(1, analysis.Segments);

        Assert.Equal(2, SmsAnalyzer.Analyze(new string('€', 81)).Segments);
    }

    [Fact]
    public void NonGsmCharacters_SwitchToUcs2()
    {
        var analysis = SmsAnalyzer.Analyze(new string('你', 70));
        Assert.Equal(SmsAnalyzer.EncodingUcs2, analysis.Encoding);
        Assert.Equal(70, analysis.Chars);
        Assert.Equal(1, analysis.Segments);

        Assert.Equal(2, SmsAnalyzer.Analyze(new string('你', 71)).Segments);
        Assert.Equal(2, SmsAnalyzer.Analyze(new string('你', 134)).Segments);
        Assert.Equal(3, SmsAnalyzer.Analyze(new string('你', 135)).Segments);
    }

    [Fact]
    public void AccentedGsm7Characters_StayInGsm7()
    {
        var analysis = SmsAnalyzer.Analyze("héllo");
        Assert.Equal(SmsAnalyzer.EncodingGsm7, analysis.Encoding);
        Assert.Equal(5, analysis.Chars);
    }

    [Fact]
    public void EmptyBody_IsOneSegment()
    {
        var analysis = SmsAnalyzer.Analyze("");
        Assert.Equal(0, analysis.Chars);
        Assert.Equal(1, analysis.Segments);
    }
}
