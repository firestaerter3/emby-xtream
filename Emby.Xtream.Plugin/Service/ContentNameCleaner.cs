using System;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    public static class ContentNameCleaner
    {
        private static readonly char[] LineSeparators = new[] { '\n', '\r' };

        // Matches one or more ┃XX┃ / │XX│ / |XX| prefix tags at the start of a string.
        // Handles: ┃UK┃, ┃UK ┃, ┃ UK┃, │EN│, |FR|, with optional whitespace around them.
        // U+2503 = ┃ (heavy vertical), U+2502 = │ (light vertical), | = pipe
        private static readonly Regex BoxPrefixRegex = new Regex(
            @"^(\s*[\u2503\u2502|][^\u2503\u2502|]+[\u2503\u2502|]\s*)+",
            RegexOptions.Compiled);

        // Matches ┃XX┃ / │XX│ / |XX| tags anywhere in the string (not just prefix).
        private static readonly Regex BoxTagAnywhereRegex = new Regex(
            @"\s*[\u2503\u2502|][^\u2503\u2502|]+[\u2503\u2502|]\s*",
            RegexOptions.Compiled);

        private static readonly Regex MultipleSpacesRegex = new Regex(
            @"\s{2,}",
            RegexOptions.Compiled);

        /// <summary>
        /// Cleans content names (movie/series titles) by removing country-code prefix
        /// tags like ┃UK┃, │EN│, |FR| and user-specified additional terms.
        /// </summary>
        public static string CleanContentName(string name, string userRemoveTerms = null, bool enableCleaning = true)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            if (!enableCleaning) return name.Trim();

            string result = name;

            // Remove ┃XX┃ style prefix tags (country codes, labels)
            result = BoxPrefixRegex.Replace(result, string.Empty);

            // Also remove any remaining ┃XX┃ tags in the middle/end of the string
            result = BoxTagAnywhereRegex.Replace(result, " ");

            // Remove user-specified terms (one per line)
            if (!string.IsNullOrWhiteSpace(userRemoveTerms))
            {
                var lines = userRemoveTerms.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var term = line.Trim();
                    if (!string.IsNullOrEmpty(term))
                    {
                        int idx;
                        while ((idx = result.IndexOf(term, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            result = result.Remove(idx, term.Length);
                        }
                    }
                }
            }

            result = MultipleSpacesRegex.Replace(result, " ");
            result = result.Trim();

            return string.IsNullOrWhiteSpace(result) ? name.Trim() : result;
        }
    }
}
