namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// The Dispatcharr output profile the plugin asks for when <c>DispatcharrVideoFirst</c> is on
    /// (issue #108, ADR-024).
    /// <para>
    /// The plugin turns probing off for Dispatcharr streams and declares video as track 0 and
    /// audio as track 1. Emby's transcoder reads the Dispatcharr URL itself and builds its
    /// filters on those numbers, so a channel that carries audio first fails with
    /// "Stream specifier ':0' ... matches no streams". This profile copies the stream unchanged
    /// but writes the first video track first, then all audio, then subtitles, which makes the
    /// declared numbers true on every channel. An audio-only channel keeps its audio at 0, which
    /// is also what the plugin declares for it.
    /// </para>
    /// <para>
    /// The flags around the mapping are the ones Dispatcharr's own built-in profiles use. The
    /// plugin looks the profile up by this exact name, so changing the name strands profiles that
    /// were already created; changing the parameters only affects servers where it is created anew.
    /// </para>
    /// </summary>
    internal static class VideoFirstOutputProfile
    {
        public const string Name = "Emby Xtream: video first";

        public const string Command = "ffmpeg";

        public const string Parameters =
            "-fflags +discardcorrupt+genpts+nobuffer " +
            "-probesize 512K " +
            "-analyzeduration 0 " +
            "-i pipe:0 " +
            "-map 0:v:0? -map 0:a? -map 0:s? " +
            "-c copy " +
            "-max_muxing_queue_size 4096 " +
            "-flush_packets 1 " +
            "-mpegts_flags +pat_pmt_at_frames+resend_headers+initial_discontinuity " +
            "-f mpegts pipe:1";
    }
}
