namespace Reel.Services;

/// <summary>
/// Metadata about a YouTube video obtained without downloading it: parsed from the
/// InnerTube player response (`videoDetails`) by <c>YouTubeService.GetVideoDetailsAsync</c>.
/// This is the confidence gate's input (F-20/F-21).
/// </summary>
public sealed class VideoDetails
{
    public string VideoId { get; set; }

    /// <summary>The video's real title as shown on YouTube (may contain brackets etc.).</summary>
    public string Title { get; set; }

    /// <summary>Channel name (`videoDetails.author`) — used by the artist-containment gate.</summary>
    public string Channel { get; set; }

    /// <summary>Length in seconds (`videoDetails.lengthSeconds`); 0 = unknown.</summary>
    public long DurationSeconds { get; set; }
}
