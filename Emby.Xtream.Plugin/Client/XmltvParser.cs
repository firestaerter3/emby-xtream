using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Client
{
    /// <summary>
    /// Streaming XMLTV parser. Parses &lt;programme&gt; elements from an XMLTV feed
    /// and extracts title, description, and status flags (live, new, repeat, premiere).
    /// Uses XmlReader to avoid loading the entire document into memory.
    /// </summary>
    internal static class XmltvParser
    {
        /// <summary>
        /// Parses an XMLTV stream and returns programs grouped by XMLTV channel ID.
        /// </summary>
        /// <param name="xmlStream">The XMLTV XML stream to parse.</param>
        /// <param name="filterStartUnix">Exclude programs that stop at or before this Unix timestamp.</param>
        /// <param name="filterEndUnix">Exclude programs that start at or after this Unix timestamp.</param>
        internal static Dictionary<string, List<EpgProgram>> Parse(
            Stream xmlStream,
            long? filterStartUnix,
            long? filterEndUnix)
        {
            var result = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);

            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Ignore,
            };

            using (var reader = XmlReader.Create(xmlStream, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (!string.Equals(reader.Name, "programme", StringComparison.OrdinalIgnoreCase)) continue;

                    var program = ParseProgramme(reader, filterStartUnix, filterEndUnix);
                    if (program == null) continue;

                    List<EpgProgram> list;
                    if (!result.TryGetValue(program.ChannelId, out list))
                    {
                        list = new List<EpgProgram>();
                        result[program.ChannelId] = list;
                    }
                    list.Add(program);
                }
            }

            return result;
        }

        private static EpgProgram ParseProgramme(
            XmlReader reader,
            long? filterStartUnix,
            long? filterEndUnix)
        {
            var startAttr = reader.GetAttribute("start");
            var stopAttr = reader.GetAttribute("stop");
            var channelAttr = reader.GetAttribute("channel");

            if (string.IsNullOrEmpty(startAttr) || string.IsNullOrEmpty(stopAttr) || string.IsNullOrEmpty(channelAttr))
                return null;

            var startUnix = ParseXmltvTimestamp(startAttr);
            var stopUnix = ParseXmltvTimestamp(stopAttr);

            if (startUnix == 0 && stopUnix == 0)
                return null;

            // Apply date range filter
            if (filterEndUnix.HasValue && startUnix >= filterEndUnix.Value)
                return null;
            if (filterStartUnix.HasValue && stopUnix <= filterStartUnix.Value)
                return null;

            var program = new EpgProgram
            {
                ChannelId = channelAttr,
                StartTimestamp = startUnix,
                StopTimestamp = stopUnix,
                IsPlainText = true,
            };

            if (reader.IsEmptyElement)
                return program;

            // Read child elements at depth + 1; break when we return to the parent's end element
            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;
                if (reader.NodeType != XmlNodeType.Element) continue;

                var name = reader.Name;

                if (string.Equals(name, "title", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                        program.Title = ReadText(reader);
                }
                else if (string.Equals(name, "desc", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                        program.Description = ReadText(reader);
                }
                else if (string.Equals(name, "live", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsLive = true;
                }
                else if (string.Equals(name, "new", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsNew = true;
                }
                else if (string.Equals(name, "previously-shown", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsPreviouslyShown = true;
                }
                else if (string.Equals(name, "premiere", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsPremiere = true;
                }
                else if (string.Equals(name, "icon", StringComparison.OrdinalIgnoreCase))
                {
                    var src = reader.GetAttribute("src");
                    if (!string.IsNullOrEmpty(src))
                        program.ImageUrl = src;
                }
            }

            return program;
        }

        /// <summary>
        /// Reads text/CDATA content from the current element, leaving the reader
        /// positioned at the element's end tag. Called only when IsEmptyElement is false.
        /// </summary>
        private static string ReadText(XmlReader reader)
        {
            var sb = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement) break;
                if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                    sb.Append(reader.Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses an XMLTV timestamp ("YYYYMMDDHHmmss +HHMM") to a Unix timestamp.
        /// Returns 0 on parse failure.
        /// </summary>
        internal static long ParseXmltvTimestamp(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            value = value.Trim();
            var spaceIdx = value.IndexOf(' ');
            var datePart = spaceIdx > 0 ? value.Substring(0, spaceIdx) : value;
            var tzPart = spaceIdx > 0 ? value.Substring(spaceIdx + 1).Trim() : null;

            if (datePart.Length < 14)
                return 0;

            int year, month, day, hour, minute, second;
            if (!int.TryParse(datePart.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)) return 0;
            if (!int.TryParse(datePart.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)) return 0;
            if (!int.TryParse(datePart.Substring(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day)) return 0;
            if (!int.TryParse(datePart.Substring(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour)) return 0;
            if (!int.TryParse(datePart.Substring(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute)) return 0;
            if (!int.TryParse(datePart.Substring(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out second)) return 0;

            int offsetMinutes = 0;
            if (!string.IsNullOrEmpty(tzPart) && tzPart.Length >= 5)
            {
                int sign = tzPart[0] == '-' ? -1 : 1;
                int tzHour, tzMin;
                if (int.TryParse(tzPart.Substring(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out tzHour)
                    && int.TryParse(tzPart.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out tzMin))
                {
                    offsetMinutes = sign * (tzHour * 60 + tzMin);
                }
            }

            try
            {
                var dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                dt = dt.AddMinutes(-offsetMinutes);
                return new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
        }
    }
}
