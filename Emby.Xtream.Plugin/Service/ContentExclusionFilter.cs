using System.Collections.Generic;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Filters individual VOD/series items out of a sync run using the configured
    /// exclusion lists (<see cref="PluginConfiguration.ExcludedVodStreamIds"/> and
    /// <see cref="PluginConfiguration.ExcludedSeriesIds"/>).
    ///
    /// Deliberately a static class with no constructor: Emby's SimpleInjector scan
    /// instantiates public service classes before Plugin.Instance exists, so anything
    /// with a DI-shaped constructor risks being built too early (see CLAUDE.md).
    /// </summary>
    internal static class ContentExclusionFilter
    {
        /// <summary>
        /// Builds a lookup set from an exclusion ID array. Null or empty input yields an empty set.
        /// </summary>
        /// <param name="excludedIds">The configured exclusion IDs, may be null.</param>
        /// <returns>A hash set of the excluded IDs.</returns>
        public static HashSet<int> BuildSet(int[] excludedIds)
        {
            if (excludedIds == null || excludedIds.Length == 0)
            {
                return new HashSet<int>();
            }

            return new HashSet<int>(excludedIds);
        }

        /// <summary>
        /// Returns true if the given item ID is present in the exclusion set.
        /// An empty set always returns false (nothing excluded).
        /// </summary>
        /// <param name="excludedSet">The exclusion set from <see cref="BuildSet"/>.</param>
        /// <param name="itemId">The stream or series ID to test.</param>
        /// <returns>True if the item should be excluded.</returns>
        public static bool IsExcluded(HashSet<int> excludedSet, int itemId)
        {
            return excludedSet != null && excludedSet.Count != 0 && excludedSet.Contains(itemId);
        }
    }
}
