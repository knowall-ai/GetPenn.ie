using System.Text.RegularExpressions;

namespace PennieBot.Helpers;

/// <summary>
/// Helper methods for parsing meeting-related data from user messages.
/// These methods are internal to allow unit testing via InternalsVisibleTo.
/// </summary>
internal static class MeetingHelpers
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Extract meeting ID from a message. Handles formats like "396 240 783 591 15" or "39624078359115".
    /// </summary>
    internal static string? ExtractMeetingId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Pattern 1: "id:" or "id :" followed by digits and spaces
        var idPattern = new Regex(
            @"id\s*:?\s*([\d\s]+)",
            RegexOptions.IgnoreCase,
            RegexTimeout);

        Match match;
        try
        {
            match = idPattern.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null; // Input too complex, reject
        }

        if (match.Success)
        {
            var id = match.Groups[1].Value.Trim();
            // Stop at "passcode" or end of digits
            var passcodeIndex = id.IndexOf("passcode", StringComparison.OrdinalIgnoreCase);
            if (passcodeIndex > 0)
            {
                id = id.Substring(0, passcodeIndex).Trim();
            }
            // Remove any non-digit/space chars at the end (with timeout for ReDoS protection)
            id = Regex.Replace(id, @"[^\d\s]+$", "", RegexOptions.None, RegexTimeout).Trim();
            if (IsValidMeetingIdFormat(id))
            {
                return id;
            }
        }

        // Pattern 2: Look for a sequence of numbers that could be a meeting ID (10-30 characters including spaces)
        var numberPattern = new Regex(
            @"(\d[\d\s]{9,29})",
            RegexOptions.None,
            RegexTimeout);

        try
        {
            match = numberPattern.Match(text);
            if (match.Success)
            {
                var id = match.Groups[1].Value.Trim();
                if (IsValidMeetingIdFormat(id))
                {
                    return id;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null; // Input too complex, reject
        }

        return null;
    }

    /// <summary>
    /// Validate that a meeting ID has the correct format (10-15 digits when spaces are removed).
    /// </summary>
    internal static bool IsValidMeetingIdFormat(string? meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            return false;
        }

        // Remove spaces and validate digit count
        var digitsOnly = meetingId.Replace(" ", "");

        // Teams meeting IDs are typically 10-15 digits
        if (digitsOnly.Length < 10 || digitsOnly.Length > 15)
        {
            return false;
        }

        // Ensure all characters are digits
        return digitsOnly.All(char.IsDigit);
    }

    /// <summary>
    /// Extract passcode from a message.
    /// </summary>
    internal static string? ExtractPasscode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Pattern 1: "passcode:" or "passcode :" followed by alphanumeric
        var passcodePattern = new Regex(
            @"passcode\s*:?\s*([a-zA-Z0-9]+)",
            RegexOptions.IgnoreCase,
            RegexTimeout);

        try
        {
            var match = passcodePattern.Match(text);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Check if the text is a simple join command (without explicit meeting ID).
    /// </summary>
    internal static bool IsSimpleJoinCommand(string text)
    {
        // Remove bot mention from text for cleaner matching
        var cleanText = StripAtMentions(text);

        // Check for simple join patterns
        var simpleJoinPatterns = new[]
        {
            "join",
            "join meeting",
            "join the meeting",
            "join this meeting",
            "join call",
            "join the call",
            "join this call"
        };

        var normalizedText = cleanText.ToLowerInvariant().Trim();

        foreach (var pattern in simpleJoinPatterns)
        {
            if (normalizedText == pattern || normalizedText.StartsWith(pattern + " "))
            {
                // Make sure it's not followed by a meeting ID
                var remainder = normalizedText.Length > pattern.Length
                    ? normalizedText.Substring(pattern.Length).Trim()
                    : "";

                // If the remainder contains digits that look like a meeting ID, it's not a simple join
                if (!string.IsNullOrEmpty(remainder) && remainder.Any(char.IsDigit))
                {
                    return false;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Strip @mention markup from Teams messages.
    /// Teams wraps @mentions in XML like: "&lt;at&gt;Pennie&lt;/at&gt; what projects do we have?"
    /// or with attributes: "&lt;at id="..."&gt;Pennie&lt;/at&gt; what projects do we have?"
    /// This strips the markup so Pennie receives clean text.
    /// </summary>
    internal static string StripAtMentions(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Remove <at>...</at> tags (Teams @mention markup)
        // Handles optional attributes like <at id="...">Name</at>
        // Uses timeout to prevent ReDoS attacks
        try
        {
            var cleanText = Regex.Replace(
                text,
                @"<at[^>]*>.*?</at>",
                "",
                RegexOptions.None,
                RegexTimeout);

            return cleanText.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            // If regex times out, return original text
            return text.Trim();
        }
    }
}
