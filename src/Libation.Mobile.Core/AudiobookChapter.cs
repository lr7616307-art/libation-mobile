namespace Libation.Mobile.Core;

/// <summary>A chapter marker relative to the beginning of an audiobook, measured in milliseconds.</summary>
public sealed record AudiobookChapter(string Title, long StartPositionMilliseconds);
