using FluentAssertions;
using PennieBot.Helpers;
using Xunit;

namespace PennieBot.Tests.Helpers;

public class MeetingHelpersTests
{
    #region IsValidMeetingIdFormat Tests

    [Theory]
    [InlineData("1234567890", true)]       // Exactly 10 digits (minimum)
    [InlineData("123456789012345", true)]  // Exactly 15 digits (maximum)
    [InlineData("12345678901", true)]      // 11 digits (valid)
    [InlineData("123 456 789 012", true)]  // 12 digits with spaces
    [InlineData("396 240 783 591 15", true)] // Real Teams format
    public void IsValidMeetingIdFormat_ValidIds_ReturnsTrue(string meetingId, bool expected)
    {
        var result = MeetingHelpers.IsValidMeetingIdFormat(meetingId);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("123456789", false)]        // 9 digits (too short)
    [InlineData("1234567890123456", false)] // 16 digits (too long)
    [InlineData("", false)]                  // Empty
    [InlineData(null, false)]                // Null
    [InlineData("   ", false)]               // Whitespace only
    [InlineData("12345678a0", false)]        // Contains letter
    [InlineData("1234-5678-90", false)]      // Contains hyphens
    public void IsValidMeetingIdFormat_InvalidIds_ReturnsFalse(string? meetingId, bool expected)
    {
        var result = MeetingHelpers.IsValidMeetingIdFormat(meetingId);
        result.Should().Be(expected);
    }

    #endregion

    #region ExtractMeetingId Tests

    [Theory]
    [InlineData("join meeting id: 396 240 783 591 15", "396 240 783 591 15")]
    [InlineData("join id:39624078359115", "39624078359115")]
    [InlineData("meeting ID 1234567890", "1234567890")]
    [InlineData("id: 123 456 789 012 passcode: ABC123", "123 456 789 012")]
    public void ExtractMeetingId_ValidFormats_ExtractsCorrectly(string text, string expectedId)
    {
        var result = MeetingHelpers.ExtractMeetingId(text);
        result.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("hello world")]              // No meeting ID
    [InlineData("id: 123")]                   // Too short
    [InlineData("")]                          // Empty
    [InlineData("join the meeting")]          // No ID at all
    public void ExtractMeetingId_InvalidFormats_ReturnsNull(string text)
    {
        var result = MeetingHelpers.ExtractMeetingId(text);
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractMeetingId_NullInput_ReturnsNull()
    {
        var result = MeetingHelpers.ExtractMeetingId(null!);
        result.Should().BeNull();
    }

    #endregion

    #region ExtractPasscode Tests

    [Theory]
    [InlineData("passcode: ABC123", "ABC123")]
    [InlineData("Passcode:xyz789", "xyz789")]
    [InlineData("PASSCODE : test123", "test123")]
    [InlineData("meeting id: 123456789012 passcode: secret", "secret")]
    public void ExtractPasscode_ValidFormats_ExtractsCorrectly(string text, string expectedPasscode)
    {
        var result = MeetingHelpers.ExtractPasscode(text);
        result.Should().Be(expectedPasscode);
    }

    [Theory]
    [InlineData("hello world")]              // No passcode
    [InlineData("passcode")]                  // No value
    [InlineData("")]                          // Empty
    public void ExtractPasscode_InvalidFormats_ReturnsNull(string text)
    {
        var result = MeetingHelpers.ExtractPasscode(text);
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractPasscode_NullInput_ReturnsNull()
    {
        var result = MeetingHelpers.ExtractPasscode(null!);
        result.Should().BeNull();
    }

    #endregion

    #region StripAtMentions Tests

    [Theory]
    [InlineData("<at>Pennie</at> what projects do we have?", "what projects do we have?")]
    [InlineData("<at id=\"123\">Pennie</at> hello", "hello")]
    [InlineData("<at>Bot</at> <at>User</at> test", "test")]
    [InlineData("no mentions here", "no mentions here")]
    [InlineData("", "")]
    public void StripAtMentions_VariousInputs_StripsCorrectly(string text, string expected)
    {
        var result = MeetingHelpers.StripAtMentions(text);
        result.Should().Be(expected);
    }

    [Fact]
    public void StripAtMentions_NullInput_ReturnsNull()
    {
        var result = MeetingHelpers.StripAtMentions(null!);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("<at>Name</at>", "")]
    [InlineData("  <at>Name</at>  text  ", "text")]
    public void StripAtMentions_OnlyMention_ReturnsEmptyOrTrimmed(string text, string expected)
    {
        var result = MeetingHelpers.StripAtMentions(text);
        result.Should().Be(expected);
    }

    #endregion

    #region IsSimpleJoinCommand Tests

    [Theory]
    [InlineData("join", true)]
    [InlineData("join meeting", true)]
    [InlineData("join the meeting", true)]
    [InlineData("join this meeting", true)]
    [InlineData("join call", true)]
    [InlineData("JOIN", true)]
    [InlineData("Join Meeting", true)]
    [InlineData("<at>Pennie</at> join", true)]
    [InlineData("<at>Pennie</at> join the meeting", true)]
    public void IsSimpleJoinCommand_SimpleJoins_ReturnsTrue(string text, bool expected)
    {
        var result = MeetingHelpers.IsSimpleJoinCommand(text);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("join meeting 1234567890", false)]   // Has meeting ID
    [InlineData("hello", false)]                      // Not a join command
    [InlineData("joining", false)]                    // Not exact match
    [InlineData("", false)]                           // Empty
    [InlineData("join id: 123456789012", false)]     // Has meeting ID
    public void IsSimpleJoinCommand_NotSimpleJoins_ReturnsFalse(string text, bool expected)
    {
        var result = MeetingHelpers.IsSimpleJoinCommand(text);
        result.Should().Be(expected);
    }

    #endregion
}
