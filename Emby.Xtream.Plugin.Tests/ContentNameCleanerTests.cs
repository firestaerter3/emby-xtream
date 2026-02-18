using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class ContentNameCleanerTests
    {
        [Theory]
        [InlineData("\u2503UK\u2503 The Movie", "The Movie")]
        [InlineData("\u2502EN\u2502 The Movie", "The Movie")]
        [InlineData("|FR| The Movie", "The Movie")]
        public void RemovesBoxPrefixTags(string input, string expected)
        {
            Assert.Equal(expected, ContentNameCleaner.CleanContentName(input));
        }

        [Fact]
        public void RemovesMultiplePrefixTags()
        {
            var result = ContentNameCleaner.CleanContentName("\u2503UK\u2503\u2503HD\u2503 The Movie");
            Assert.Equal("The Movie", result);
        }

        [Fact]
        public void RemovesBoxTagsMidString()
        {
            var result = ContentNameCleaner.CleanContentName("The \u2503UK\u2503 Movie");
            Assert.Equal("The Movie", result);
        }

        [Fact]
        public void RemovesUserTerms()
        {
            var result = ContentNameCleaner.CleanContentName("|EN| The Movie [Dubbed]", "[Dubbed]");
            Assert.Equal("The Movie", result);
        }

        [Fact]
        public void UserTermsCaseInsensitive()
        {
            var result = ContentNameCleaner.CleanContentName("The Movie dubbed", "DUBBED");
            Assert.Equal("The Movie", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ReturnsInputForNullOrWhitespace(string input)
        {
            Assert.Equal(input, ContentNameCleaner.CleanContentName(input));
        }

        [Fact]
        public void DisabledModeOnlyTrims()
        {
            var result = ContentNameCleaner.CleanContentName("  |UK| The Movie  ", enableCleaning: false);
            Assert.Equal("|UK| The Movie", result);
        }

        [Fact]
        public void FallsBackToOriginalWhenAllRemoved()
        {
            // A name that is only tags should fall back to trimmed original
            var result = ContentNameCleaner.CleanContentName("|UK|");
            Assert.Equal("|UK|", result);
        }

        [Fact]
        public void RemovesLightVerticalMidString()
        {
            var result = ContentNameCleaner.CleanContentName("The \u2502EN\u2502 Movie");
            Assert.Equal("The Movie", result);
        }

        [Fact]
        public void MultiLineUserTerms()
        {
            var result = ContentNameCleaner.CleanContentName("The Movie [Dubbed] CAM", "[Dubbed]\nCAM");
            Assert.Equal("The Movie", result);
        }

        [Fact]
        public void RemovesBoxTagAtEnd()
        {
            var result = ContentNameCleaner.CleanContentName("The Movie |EN|");
            Assert.Equal("The Movie", result);
        }

        [Fact]
        public void HandlesMixedDelimiterTypes()
        {
            // Mismatched open ┃ and close │ — regex allows any combo
            var result = ContentNameCleaner.CleanContentName("\u2503UK\u2502 The Movie");
            Assert.Equal("The Movie", result);
        }
    }
}
