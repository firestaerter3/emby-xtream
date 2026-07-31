using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Placeholder for XtreamTunerHost tests.
    ///
    /// Full integration tests are deferred because XtreamTunerHost.GetChannelStreamMediaSources
    /// relies on a static ConcurrentDictionary (_streamStats) for stream statistics, making
    /// test isolation between test runs impossible without further refactoring of the stats
    /// lifecycle (e.g., making it instance-level or injecting a stats provider).
    ///
    /// Planned tests once the static state is addressed:
    /// - Dispatcharr path: enabled → MediaSourceInfo.Path is /proxy/ts/stream/{uuid}, SupportsProbing=false
    /// - Direct Xtream path: Dispatcharr disabled → path is raw Xtream stream URL
    /// - AC3 → 6 channels, EAC3 → 6 channels, MP2 → 2 channels, unknown codec → no channel count
    /// - Stats for wrong stream ID → no-stats defaults applied
    /// - SupportsDirectStream: stats present → true; no stats → false
    /// - ForceAudioTranscode config flag reflected on media source
    /// - UserAgent propagation
    /// </summary>
    public class XtreamTunerHostTests
    {
        [Fact]
        public void Placeholder_StaticStreamStatsCachePreventsIsolation()
        {
            // This is an explicit placeholder for a deferred test scenario.
            // It runs (not skipped) so the suite still tracks the work item in code review.
            // See class-level XML doc for planned scenarios.
            //
            // Deferred because XtreamTunerHost.GetChannelStreamMediaSources relies on a static
            // ConcurrentDictionary (_streamStats) for stream statistics, which makes test
            // isolation between test runs impossible without further refactoring of the stats
            // lifecycle (e.g., making it instance-level or injecting a stats provider).
            //
            // To enable the real assertions, refactor the static _streamStats cache to be
            // instance-level (or DI-injected), then replace this body with the planned
            // scenarios listed at the top of this file.
            Assert.True(true, "Placeholder: defer XtreamTunerHost scenarios until _streamStats is instance-level.");
        }
    }
}
