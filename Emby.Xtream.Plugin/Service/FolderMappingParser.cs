using System;
using System.Collections.Generic;

namespace Emby.Xtream.Plugin.Service
{
    public static class FolderMappingParser
    {
        private static readonly char[] LineSeparators = new[] { '\n', '\r' };

        /// <summary>
        /// Parses folder mapping text in the format "FolderName=CategoryId1,CategoryId2" (one per line).
        /// Returns a dictionary mapping each category ID to its assigned folder name.
        /// Lines starting with # are treated as comments.
        /// </summary>
        public static Dictionary<int, string> Parse(string mappingText)
        {
            var result = new Dictionary<int, string>();

            if (string.IsNullOrWhiteSpace(mappingText))
            {
                return result;
            }

            var lines = mappingText.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                {
                    continue;
                }

                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex <= 0 || eqIndex >= trimmed.Length - 1)
                {
                    continue;
                }

                var folderName = trimmed.Substring(0, eqIndex).Trim();
                var idsText = trimmed.Substring(eqIndex + 1).Trim();

                if (string.IsNullOrEmpty(folderName) || string.IsNullOrEmpty(idsText))
                {
                    continue;
                }

                var idParts = idsText.Split(',');
                foreach (var idPart in idParts)
                {
                    int categoryId;
                    if (int.TryParse(idPart.Trim(), out categoryId))
                    {
                        result[categoryId] = folderName;
                    }
                }
            }

            return result;
        }
    }
}
