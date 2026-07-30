using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class ContentExclusionFilterTests
    {
        [Fact]
        public void BuildSet_Null_ReturnsEmptySet()
        {
            var set = ContentExclusionFilter.BuildSet(null);
            Assert.Empty(set);
        }

        [Fact]
        public void BuildSet_Empty_ReturnsEmptySet()
        {
            var set = ContentExclusionFilter.BuildSet(new int[0]);
            Assert.Empty(set);
        }

        [Fact]
        public void BuildSet_Duplicates_Deduplicated()
        {
            var set = ContentExclusionFilter.BuildSet(new[] { 7, 7, 9 });
            Assert.Equal(2, set.Count);
        }

        [Fact]
        public void IsExcluded_EmptySet_AlwaysFalse()
        {
            var set = ContentExclusionFilter.BuildSet(null);
            Assert.False(ContentExclusionFilter.IsExcluded(set, 1));
            Assert.False(ContentExclusionFilter.IsExcluded(set, 0));
        }

        [Fact]
        public void IsExcluded_MemberAndNonMember()
        {
            var set = ContentExclusionFilter.BuildSet(new[] { 42 });
            Assert.True(ContentExclusionFilter.IsExcluded(set, 42));
            Assert.False(ContentExclusionFilter.IsExcluded(set, 43));
        }

        [Fact]
        public void Configuration_ExclusionLists_DefaultEmpty()
        {
            var config = new PluginConfiguration();
            Assert.NotNull(config.ExcludedVodStreamIds);
            Assert.Empty(config.ExcludedVodStreamIds);
            Assert.NotNull(config.ExcludedSeriesIds);
            Assert.Empty(config.ExcludedSeriesIds);
        }
    }
}
