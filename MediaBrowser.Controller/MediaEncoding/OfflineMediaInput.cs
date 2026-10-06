namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>Input restrictions shared by every media-reading native helper.</summary>
public static class OfflineMediaInput
{
    /// <summary>
    /// Only local file transport and self-contained media/subtitle demuxers. In
    /// particular hls, dash, concat, image2 sequences and reference/URL playlists
    /// are excluded even when someone gives such a file a playable extension.
    /// These are input options and must precede each input, not merely the process.
    /// </summary>
    public const string Arguments = "-protocol_whitelist file -format_whitelist aac,ac3,aiff,ape,asf,avi,aa,dts,dsf,flac,flv,matroska,webm,mov,mp4,m4a,3gp,3g2,mj2,mp3,mpeg,mpegts,mpegvideo,ogg,wav,ass,srt,webvtt ";

    /// <summary>DVD-Video ISO input only. The nested MPEG program stream is required by dvdvideo.</summary>
    public const string DvdVideoArguments = "-protocol_whitelist file -format_whitelist dvdvideo,mpeg,mpegvideo ";
}
