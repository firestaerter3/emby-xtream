using System;
using System.IO;
using System.Text;

namespace Emby.Xtream.Plugin.Service
{
    internal static class NfoWriter
    {
        /// <summary>Writes a Kodi-compatible movie NFO. Skips if no TMDB ID or file exists.</summary>
        public static void WriteMovieNfo(string nfoPath, string title, string tmdbId, int? year)
        {
            if (string.IsNullOrEmpty(tmdbId)) return;
            if (File.Exists(nfoPath)) return;

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<movie>");
            sb.AppendFormat("  <title>{0}</title>", EscapeXml(title)).AppendLine();
            if (year.HasValue)
                sb.AppendFormat("  <year>{0}</year>", year.Value).AppendLine();
            sb.AppendFormat("  <uniqueid type=\"tmdb\" default=\"true\">{0}</uniqueid>", tmdbId).AppendLine();
            sb.AppendLine("</movie>");

            File.WriteAllText(nfoPath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>Writes a tvshow.nfo. Skips if no provider ID or file exists.</summary>
        public static void WriteShowNfo(string nfoPath, string title, string tvdbId, string tmdbId)
        {
            if (string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(tmdbId)) return;
            if (File.Exists(nfoPath)) return;

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<tvshow>");
            sb.AppendFormat("  <title>{0}</title>", EscapeXml(title)).AppendLine();
            if (!string.IsNullOrEmpty(tvdbId))
                sb.AppendFormat("  <uniqueid type=\"tvdb\" default=\"true\">{0}</uniqueid>", tvdbId).AppendLine();
            if (!string.IsNullOrEmpty(tmdbId))
            {
                var defaultAttr = string.IsNullOrEmpty(tvdbId) ? " default=\"true\"" : "";
                sb.AppendFormat("  <uniqueid type=\"tmdb\"{0}>{1}</uniqueid>", defaultAttr, tmdbId).AppendLine();
            }
            sb.AppendLine("</tvshow>");

            File.WriteAllText(nfoPath, sb.ToString(), Encoding.UTF8);
        }

        private static string EscapeXml(string s)
            => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
