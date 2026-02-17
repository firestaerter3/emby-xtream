using System;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    public static class ChannelNameCleaner
    {
        private static readonly char[] LineSeparators = new[] { '\n', '\r' };

        private static readonly Regex CountryPrefixRegex = new Regex(
            @"^(UK|US|DE|FR|NL|ES|IT|CA|AU|BE|CH|AT|PT|BR|MX|AR|PL|CZ|RO|HU|TR|GR|SE|NO|DK|FI|IE|IN|PK|AF|ZA|AE|SA|EG|MA|NG|KE|JP|KR|CN|TW|HK|SG|MY|TH|VN|PH|ID|NZ|RU|UA|BY|KZ|IL|IR|IQ)\s*[:\|\-]\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QualityTagSeparatorRegex = new Regex(
            @"\s*\|\s*(HD|FHD|UHD|4K|SD|720p|1080p|2160p|HEVC|H\.?264|H\.?265)\s*\|?\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QualityTagEndRegex = new Regex(
            @"\s+(HD|FHD|UHD|4K|SD)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ResolutionSuffixRegex = new Regex(
            @"\s*(1080[pi]?|720[pi]?|4K|2160[pi]?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CodecInfoRegex = new Regex(
            @"\s*(HEVC|H\.?264|H\.?265|AVC|MPEG-?[24]|VP9|AV1)\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BracketedTagsRegex = new Regex(
            @"\s*[\[\(](HD|FHD|UHD|4K|SD|HEVC|H\.?264|H\.?265|720p|1080p)[\]\)]\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LeadingTrailingPipeRegex = new Regex(
            @"(^\s*\|\s*|\s*\|\s*$)",
            RegexOptions.Compiled);

        private static readonly Regex MultipleSpacesRegex = new Regex(
            @"\s{2,}",
            RegexOptions.Compiled);

        public static string CleanChannelName(string name, string userRemoveTerms = null, bool enableCleaning = true)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            if (!enableCleaning) return name.Trim();

            string result = name;

            if (!string.IsNullOrWhiteSpace(userRemoveTerms))
            {
                var lines = userRemoveTerms.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var term = line.Trim();
                    if (!string.IsNullOrEmpty(term))
                    {
                        // Case-insensitive replace for netstandard2.0
                        int idx;
                        while ((idx = result.IndexOf(term, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            result = result.Remove(idx, term.Length);
                        }
                    }
                }
            }

            result = CountryPrefixRegex.Replace(result, string.Empty);
            result = QualityTagSeparatorRegex.Replace(result, " ");
            result = BracketedTagsRegex.Replace(result, string.Empty);
            result = CodecInfoRegex.Replace(result, string.Empty);
            result = ResolutionSuffixRegex.Replace(result, string.Empty);
            result = QualityTagEndRegex.Replace(result, string.Empty);
            result = LeadingTrailingPipeRegex.Replace(result, string.Empty);
            result = MultipleSpacesRegex.Replace(result, " ");
            result = result.Trim();

            return string.IsNullOrWhiteSpace(result) ? name.Trim() : result;
        }
    }
}
