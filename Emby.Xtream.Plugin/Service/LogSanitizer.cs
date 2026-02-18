using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    public static class LogSanitizer
    {
        private static readonly Regex IpRegex = new Regex(
            @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
            RegexOptions.Compiled);

        private static readonly Regex XtreamCredRegex = new Regex(
            @"/live/[^/]+/[^/]+/",
            RegexOptions.Compiled);

        private static readonly Regex EmailRegex = new Regex(
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
            RegexOptions.Compiled);

        private static readonly Regex ProviderHostRegex = new Regex(
            @"(https?://)([^/:]+)(:\d+)?(/player_api\.php|/live/|/movie/|/series/)",
            RegexOptions.Compiled);

        /// <summary>
        /// Sanitizes a single log line by redacting PII: known credentials, IP addresses,
        /// Xtream URL credentials, emails, and provider hostnames.
        /// </summary>
        public static string SanitizeLine(string line,
            string username, string password,
            string dispatcharrUser, string dispatcharrPass)
        {
            if (string.IsNullOrEmpty(line)) return line;

            var s = line;

            // Redact specific config values if non-empty
            if (!string.IsNullOrEmpty(username))
                s = s.Replace(username, "<redacted>");
            if (!string.IsNullOrEmpty(password))
                s = s.Replace(password, "<redacted>");
            if (!string.IsNullOrEmpty(dispatcharrUser))
                s = s.Replace(dispatcharrUser, "<redacted>");
            if (!string.IsNullOrEmpty(dispatcharrPass))
                s = s.Replace(dispatcharrPass, "<redacted>");

            // Redact IP addresses
            s = IpRegex.Replace(s, "<ip-redacted>");

            // Redact Xtream credentials in URLs: /live/user/pass/
            s = XtreamCredRegex.Replace(s, "/live/<user>/<pass>/");

            // Redact email patterns
            s = EmailRegex.Replace(s, "<email-redacted>");

            // Redact hostnames in stream URLs
            s = ProviderHostRegex.Replace(s, "$1<provider-host>$3$4");

            return s;
        }
    }
}
