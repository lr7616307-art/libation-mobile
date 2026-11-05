using System.Globalization;
using System.Text.Json.Serialization;

namespace Libation.Mobile.Core;

/// <summary>A chapter marker relative to the beginning of an audiobook, measured in milliseconds.</summary>
public sealed record AudiobookChapter(string Title, long StartPositionMilliseconds)
{
    [JsonIgnore]
    public string StartTimeLabel
    {
        get
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, StartPositionMilliseconds));
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }
}
