using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class ChannelNameCleanerTests
    {
        [Theory]
        [InlineData("UK: BBC One", "BBC One")]
        [InlineData("US| CNN", "CNN")]
        [InlineData("DE - RTL", "RTL")]
        [InlineData("FR: TF1", "TF1")]
        public void RemovesCountryPrefix(string input, string expected)
        {
            Assert.Equal(expected, ChannelNameCleaner.CleanChannelName(input));
        }

        [Theory]
        [InlineData("BBC One | HD |", "BBC One")]
        [InlineData("CNN | FHD |", "CNN")]
        [InlineData("Sky Sports | UHD", "Sky Sports")]
        public void RemovesQualityTagSeparators(string input, string expected)
        {
            Assert.Equal(expected, ChannelNameCleaner.CleanChannelName(input));
        }

        [Theory]
        [InlineData("BBC One HD", "BBC One")]
        [InlineData("CNN FHD", "CNN")]
        [InlineData("Sky 4K", "Sky")]
        public void RemovesQualityTagAtEnd(string input, string expected)
        {
            Assert.Equal(expected, ChannelNameCleaner.CleanChannelName(input));
        }

        [Theory]
        [InlineData("BBC One HEVC", "BBC One")]
        [InlineData("CNN H264", "CNN")]
        [InlineData("Sky H.265", "Sky")]
        public void RemovesCodecInfo(string input, string expected)
        {
            Assert.Equal(expected, ChannelNameCleaner.CleanChannelName(input));
        }

        [Theory]
        [InlineData("BBC One [HD]", "BBC One")]
        [InlineData("CNN (FHD)", "CNN")]
        [InlineData("Sky [HEVC]", "Sky")]
        public void RemovesBracketedTags(string input, string expected)
        {
            Assert.Equal(expected, ChannelNameCleaner.CleanChannelName(input));
        }

        [Theory]
        [InlineData("BBC One 1080p", "BBC One")]
        [InlineData("CNN 720p", "CNN")]
        [InlineData("Sky 2160p", "Sky")]
        public void RemovesResolutionSuffix(string input, string expected)
        {
            Assert.Equal(expected, ChannelNameCleaner.CleanChannelName(input));
        }

        [Fact]
        public void RemovesUserTerms()
        {
            var result = ChannelNameCleaner.CleanChannelName("UK: BBC One [Multi-Sub]", "[Multi-Sub]");
            Assert.Equal("BBC One", result);
        }

        [Fact]
        public void UserTermsCaseInsensitive()
        {
            var result = ChannelNameCleaner.CleanChannelName("BBC One BACKUP", "backup");
            Assert.Equal("BBC One", result);
        }

        [Fact]
        public void MultiLineUserTerms()
        {
            var result = ChannelNameCleaner.CleanChannelName("BBC One [Multi-Sub] BACKUP", "[Multi-Sub]\nBACKUP");
            Assert.Equal("BBC One", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ReturnsInputForNullOrWhitespace(string input)
        {
            Assert.Equal(input, ChannelNameCleaner.CleanChannelName(input));
        }

        [Fact]
        public void DisabledModeOnlyTrims()
        {
            var result = ChannelNameCleaner.CleanChannelName("  UK: BBC One HD  ", enableCleaning: false);
            Assert.Equal("UK: BBC One HD", result);
        }

        [Fact]
        public void FallsBackToOriginalWhenAllRemoved()
        {
            // If cleaning removes everything, return trimmed original
            var result = ChannelNameCleaner.CleanChannelName("HD", userRemoveTerms: null);
            Assert.Equal("HD", result);
        }

        [Fact]
        public void CombinedCleaningAllPatterns()
        {
            var result = ChannelNameCleaner.CleanChannelName("UK: BBC One | HD | HEVC 1080p");
            Assert.Equal("BBC One", result);
        }

        [Fact]
        public void CountryPrefixCaseInsensitive()
        {
            Assert.Equal("BBC One", ChannelNameCleaner.CleanChannelName("uk: BBC One"));
        }

        [Theory]
        [InlineData("BBC One AVC", "BBC One")]
        [InlineData("BBC One VP9", "BBC One")]
        [InlineData("BBC One AV1", "BBC One")]
        [InlineData("BBC One MPEG2", "BBC One")]
        public void RemovesAdditionalCodecs(string input, string expected)
        {
            Assert.Equal(expected, ChannelNameCleaner.CleanChannelName(input));
        }

        [Theory]
        [InlineData("BBC One 1080i", "BBC One")]
        [InlineData("BBC One 720i", "BBC One")]
        public void RemovesInterlacedResolution(string input, string expected)
        {
            Assert.Equal(expected, ChannelNameCleaner.CleanChannelName(input));
        }

        [Fact]
        public void CleansLeadingTrailingPipes()
        {
            Assert.Equal("BBC One", ChannelNameCleaner.CleanChannelName("| BBC One |"));
        }
    }
}
