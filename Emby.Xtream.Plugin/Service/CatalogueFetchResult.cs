using System.Collections.Generic;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// The outcome of fetching a catalogue (VOD streams or series) from the provider,
    /// carrying whether every requested category actually answered.
    /// </summary>
    /// <remarks>
    /// A per-category request that fails used to be swallowed into an empty list, which is
    /// indistinguishable from a category that is genuinely empty. That empty result then flowed
    /// into the valid-path set and orphan cleanup deleted the category's existing files. A single
    /// provider 502 across many categories usually lands under <see
    /// cref="PluginConfiguration.OrphanSafetyThreshold"/>, so the ratio guard did not catch it.
    ///
    /// <see cref="HadFailures"/> gates cleanup only. The delta watermark is still computed over
    /// <see cref="Items"/> — the titles that did come back — because freezing it entirely would
    /// make every later sync re-process the categories that succeeded.
    /// </remarks>
    /// <typeparam name="T">The catalogue item type (<c>VodStreamInfo</c> or <c>SeriesInfo</c>).</typeparam>
    internal sealed class CatalogueFetchResult<T>
    {
        /// <summary>Items returned by the categories that answered successfully.</summary>
        public List<T> Items { get; set; } = new List<T>();

        /// <summary>True when at least one requested category failed to answer.</summary>
        public bool HadFailures => FailedCategoryCount > 0;

        /// <summary>How many requested categories failed.</summary>
        public int FailedCategoryCount { get; set; }

        /// <summary>How many categories were requested (0 for a whole-catalogue fetch).</summary>
        public int RequestedCategoryCount { get; set; }
    }
}
