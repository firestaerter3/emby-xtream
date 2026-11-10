using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>One <c>.strm</c> to write for an episode: its label (null for the main file) and URL.</summary>
    internal sealed class EpisodeVersion
    {
        public EpisodeVersion(string label, string url)
        {
            Label = label;
            Url = url;
        }

        /// <summary>Added to the file name as <c> - {Label}</c>. Null for the first version.</summary>
        public string Label { get; }

        public string Url { get; }
    }

    /// <summary>
    /// Turns the providers Dispatcharr has for one episode into the files to write (issue #111,
    /// ADR-025). Same idea as movies: one <c>.strm</c> per provider, each playing that provider's
    /// stream through Dispatcharr's VOD proxy.
    /// </summary>
    internal static class EpisodeVersions
    {
        /// <summary>Emby lists at most 8 versions of an episode.</summary>
        internal const int MaxVersions = 8;

        /// <summary>
        /// Returns one entry per provider, best first, or an empty list when there is nothing to
        /// split (one provider, no UUID). Order follows Dispatcharr's own pick for the plain Xtream
        /// URL: highest account priority, then lowest relation id. So the first file, which keeps
        /// the name the episode always had, plays what the episode played before.
        /// </summary>
        public static List<EpisodeVersion> Build(DispatcharrSeriesEpisode episode, string dispatcharrUrl)
        {
            var result = new List<EpisodeVersion>();
            if (episode == null || string.IsNullOrEmpty(episode.Uuid) || string.IsNullOrEmpty(dispatcharrUrl))
                return result;

            var providers = (episode.Providers ?? new List<DispatcharrEpisodeProvider>())
                .Where(p => p != null && !string.IsNullOrEmpty(p.StreamId))
                .OrderByDescending(p => p.Account?.Priority ?? 0)
                .ThenBy(p => p.Id)
                .Take(MaxVersions)
                .ToList();
            if (providers.Count < 2)
                return result;

            var baseUrl = dispatcharrUrl.TrimEnd('/');
            var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < providers.Count; i++)
            {
                var url = string.Format(CultureInfo.InvariantCulture,
                    "{0}/proxy/vod/episode/{1}?stream_id={2}",
                    baseUrl, episode.Uuid, Uri.EscapeDataString(providers[i].StreamId));

                string label = null;
                if (i > 0)
                {
                    label = StrmSyncService.SanitizeFileName(providers[i].Account?.Name);
                    if (string.IsNullOrEmpty(label))
                        label = string.Format(CultureInfo.InvariantCulture, "Version {0}", i + 1);

                    // Two accounts with the same name would write the same file twice.
                    var unique = label;
                    for (int n = 2; !usedLabels.Add(unique); n++)
                        unique = string.Format(CultureInfo.InvariantCulture, "{0} {1}", label, n);
                    label = unique;
                }

                result.Add(new EpisodeVersion(label, url));
            }

            return result;
        }

        /// <summary><c>Show - S01E01 - Title.strm</c> → <c>Show - S01E01 - Title - UK.strm</c>.</summary>
        public static string FileName(string baseFileName, string label)
        {
            if (string.IsNullOrEmpty(label))
                return baseFileName;

            const string ext = ".strm";
            var stem = baseFileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                ? baseFileName.Substring(0, baseFileName.Length - ext.Length)
                : baseFileName;
            return stem + " - " + label + ext;
        }
    }
}
