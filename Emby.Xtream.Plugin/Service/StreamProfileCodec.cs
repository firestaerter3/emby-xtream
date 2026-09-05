using System;
using System.Collections.Generic;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Works out which video codec a Dispatcharr stream profile emits, by reading the
    /// profile's own ffmpeg arguments.
    /// <para>
    /// Dispatcharr's <c>stream_stats.video_codec</c> reports what it ingested from the
    /// provider. When the profile transcodes, that is not what Emby receives, and declaring
    /// it forces the wrong decoder (issue #66). The profile is the one place that states the
    /// output, and reading it costs a cached API call rather than a probe of the stream.
    /// </para>
    /// <para>
    /// Everything that cannot be identified with confidence returns null, which the caller
    /// reads as "no answer" and falls back to the reported codec. Guessing is worse than
    /// deferring: a wrong declaration is the bug this class exists to fix.
    /// </para>
    /// </summary>
    internal static class StreamProfileCodec
    {
        private static readonly char[] TokenSeparators = { ' ', '\t', '\r', '\n' };

        /// <summary>
        /// Returns the codec a profile emits ("h264", "hevc", "mpeg2video"), or null when the
        /// profile passes video through untouched, is not an ffmpeg profile, or uses an
        /// encoder this parser does not recognise.
        /// </summary>
        internal static string Parse(string command, string parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters)) return null;

            // Non-ffmpeg profiles (the built-in redirect and proxy profiles, streamlink, vlc,
            // custom scripts) do not re-encode, so the ingested codec is also the emitted one.
            // An empty command is treated as ffmpeg: some profiles carry the binary in the
            // parameters string instead.
            if (!string.IsNullOrWhiteSpace(command)
                && command.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            var tokens = parameters.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);

            // ffmpeg applies the last matching option, so hand-tuned profiles that set a codec
            // twice ("-c:v copy" early, a real encoder later) must resolve to the later one.
            string encoder = null;
            for (int i = 0; i < tokens.Length; i++)
            {
                var token = Unquote(tokens[i]);

                // "-c:v=h264_nvenc": flag and value in one token.
                var equals = token.IndexOf('=');
                if (equals > 0 && IsVideoCodecFlag(token.Substring(0, equals)))
                {
                    encoder = Unquote(token.Substring(equals + 1));
                    continue;
                }

                if (i + 1 < tokens.Length && IsVideoCodecFlag(token))
                {
                    encoder = Unquote(tokens[i + 1]);
                }
            }

            return MapEncoder(encoder);
        }

        /// <summary>
        /// Builds a stream-id → emitted-codec map from the per-channel profile assignments and
        /// the profile list. Channels with no profile of their own fall back to the server's
        /// default profile. Streams whose profile cannot be resolved or parsed are left out of
        /// the map entirely, so callers see the absence rather than a guess.
        /// </summary>
        internal static Dictionary<int, string> BuildCodecMap(
            Dictionary<int, int> streamProfileIds,
            List<DispatcharrStreamProfile> profiles,
            int? defaultProfileId)
        {
            var result = new Dictionary<int, string>();
            if (streamProfileIds == null || streamProfileIds.Count == 0) return result;

            var parsedByProfile = new Dictionary<int, string>();
            if (profiles != null)
            {
                foreach (var profile in profiles)
                {
                    var codec = Parse(profile.Command, profile.Parameters);
                    if (codec != null) parsedByProfile[profile.Id] = codec;
                }
            }

            string defaultCodec = null;
            if (defaultProfileId.HasValue)
            {
                parsedByProfile.TryGetValue(defaultProfileId.Value, out defaultCodec);
            }

            foreach (var entry in streamProfileIds)
            {
                string codec;
                // A profile ID of 0 is the sentinel this map uses for "channel has no profile
                // of its own", written by the caller when both id fields came back null.
                if (entry.Value == 0)
                {
                    codec = defaultCodec;
                }
                else if (!parsedByProfile.TryGetValue(entry.Value, out codec))
                {
                    codec = null;
                }

                if (codec != null) result[entry.Key] = codec;
            }

            return result;
        }

        /// <summary>
        /// Strips the quotes profiles sometimes carry around a value ("-c:v \"h264_nvenc\"").
        /// The parameters string is written by hand in Dispatcharr's UI, so both quote styles
        /// turn up, and an encoder left with its quotes on would read as unrecognised.
        /// </summary>
        private static string Unquote(string token)
        {
            if (string.IsNullOrEmpty(token)) return token;
            return token.Trim('"', '\'');
        }

        private static bool IsVideoCodecFlag(string token)
        {
            if (string.IsNullOrEmpty(token) || token[0] != '-') return false;
            // "-c" and "-codec" without a stream specifier apply to every stream, video included.
            return token.Equals("-c:v", StringComparison.OrdinalIgnoreCase)
                || token.Equals("-codec:v", StringComparison.OrdinalIgnoreCase)
                || token.Equals("-vcodec", StringComparison.OrdinalIgnoreCase)
                || token.Equals("-c", StringComparison.OrdinalIgnoreCase)
                || token.Equals("-codec", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-c:v:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-codec:v:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Maps an ffmpeg encoder name onto the codec name Emby expects. Unknown encoders
        /// return null rather than their own name: handing Emby "av1_nvenc" as a codec is
        /// worse than handing it nothing, because Emby will try to use it.
        /// </summary>
        private static string MapEncoder(string encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return null;
            var lower = encoder.ToLowerInvariant();

            // "copy" is passthrough, so the ingested codec is the emitted one. Reported as
            // null ("no answer from the profile") so the caller uses stats, which already
            // holds that same codec.
            if (lower == "copy") return null;

            if (lower == "libx264" || lower.StartsWith("h264", StringComparison.Ordinal)) return "h264";
            if (lower == "libx265"
                || lower.StartsWith("hevc", StringComparison.Ordinal)
                || lower.StartsWith("h265", StringComparison.Ordinal)) return "hevc";
            if (lower.StartsWith("mpeg2", StringComparison.Ordinal)) return "mpeg2video";

            return null;
        }
    }
}
