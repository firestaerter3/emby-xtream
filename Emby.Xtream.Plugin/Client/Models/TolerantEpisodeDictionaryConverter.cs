using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Deserializes the <c>episodes</c> field of a <c>get_series_info</c> response
    /// even when the provider returns it in an unexpected shape.
    ///
    /// The <c>episodes</c> field is expected to be
    /// <c>Dictionary&lt;string, List&lt;EpisodeInfo&gt;&gt;</c> (season-number-string
    /// to array of episodes). Some providers occasionally return:
    /// <list type="bullet">
    ///   <item><c>null</c></item>
    ///   <item>an empty or non-empty JSON array <c>[]</c></item>
    ///   <item>an object whose entry values are not valid <c>EpisodeInfo</c> arrays</item>
    /// </list>
    /// Any of these would crash the entire series sync.
    ///
    /// This converter falls back to an empty dictionary on any mismatch, preserving
    /// a best-effort parse of well-formed entries.
    /// </summary>
    internal sealed class TolerantEpisodeDictionaryConverter
        : JsonConverter<Dictionary<string, List<EpisodeInfo>>>
    {
        public override bool HandleNull { get { return true; } }

        public override Dictionary<string, List<EpisodeInfo>> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return new Dictionary<string, List<EpisodeInfo>>();

                case JsonTokenType.StartArray:
                    // Some providers return [] for the episodes field.
                    reader.Skip();
                    return new Dictionary<string, List<EpisodeInfo>>();

                case JsonTokenType.StartObject:
                    return ReadDictionary(ref reader, options);

                default:
                    // Unexpected token (e.g. bare number or string) — skip and return empty.
                    reader.Skip();
                    return new Dictionary<string, List<EpisodeInfo>>();
            }
        }

        private static Dictionary<string, List<EpisodeInfo>> ReadDictionary(
            ref Utf8JsonReader reader,
            JsonSerializerOptions options)
        {
            var result = new Dictionary<string, List<EpisodeInfo>>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    reader.Skip();
                    continue;
                }

                var key = reader.GetString();
                if (!reader.Read())
                    break;

                // Try to parse the value as a List<EpisodeInfo>.
                // If it fails (e.g. the value is a bare object, null, or wrong structure),
                // skip it and continue with the next entry.
                if (TryParseEpisodeList(ref reader, options, out var episodes))
                {
                    if (key != null)
                    {
                        result[key] = episodes;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            return result;
        }

        private static bool TryParseEpisodeList(
            ref Utf8JsonReader reader,
            JsonSerializerOptions options,
            out List<EpisodeInfo> episodes)
        {
            episodes = null;

            // If null or not an array, fail fast.
            if (reader.TokenType == JsonTokenType.Null)
                return false;

            if (reader.TokenType != JsonTokenType.StartArray)
                return false;

            var list = new List<EpisodeInfo>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;

                // Each entry should be an object { id, episode_num, title, ... }.
                // If the entry itself is a non-object (null, number, string, array),
                // skip it individually.
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                try
                {
                    using (var episodeDoc = JsonDocument.ParseValue(ref reader))
                    {
                        if (!HasKnownEpisodeField(episodeDoc.RootElement))
                        {
                            continue;
                        }

                        var episode = episodeDoc.RootElement.Deserialize<EpisodeInfo>(options);
                        if (episode != null)
                        {
                            list.Add(episode);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Malformed entry — skip it rather than aborting the whole series.
                    // JsonDocument.ParseValue consumes the object or throws before advancing.
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        reader.Skip();
                    }
                }
            }

            if (list.Count > 0)
            {
                episodes = list;
                return true;
            }

            return false;
        }

        private static bool HasKnownEpisodeField(JsonElement episode)
        {
            return episode.TryGetProperty("id", out _)
                || episode.TryGetProperty("episode_num", out _)
                || episode.TryGetProperty("title", out _)
                || episode.TryGetProperty("container_extension", out _);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Dictionary<string, List<EpisodeInfo>> value,
            JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}
