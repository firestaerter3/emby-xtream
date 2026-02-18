using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class FolderMappingParserTests
    {
        [Fact]
        public void ParsesBasicMapping()
        {
            var result = FolderMappingParser.Parse("Sports=1,2,3");
            Assert.Equal("Sports", result[1]);
            Assert.Equal("Sports", result[2]);
            Assert.Equal("Sports", result[3]);
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public void ParsesMultipleLines()
        {
            var result = FolderMappingParser.Parse("Sports=1,2\nNews=3,4");
            Assert.Equal("Sports", result[1]);
            Assert.Equal("Sports", result[2]);
            Assert.Equal("News", result[3]);
            Assert.Equal("News", result[4]);
        }

        [Fact]
        public void IgnoresComments()
        {
            var result = FolderMappingParser.Parse("# This is a comment\nSports=1");
            Assert.Single(result);
            Assert.Equal("Sports", result[1]);
        }

        [Fact]
        public void IgnoresMalformedLines()
        {
            var result = FolderMappingParser.Parse("NoEqualsHere\n=1\nSports=");
            Assert.Empty(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ReturnsEmptyForNullOrWhitespace(string input)
        {
            var result = FolderMappingParser.Parse(input);
            Assert.Empty(result);
        }

        [Fact]
        public void HandlesWhitespaceAroundValues()
        {
            var result = FolderMappingParser.Parse("  Sports  =  1 , 2  ");
            Assert.Equal("Sports", result[1]);
            Assert.Equal("Sports", result[2]);
        }

        [Fact]
        public void DuplicateCategoryLastWins()
        {
            var result = FolderMappingParser.Parse("Sports=1\nEntertainment=1");
            Assert.Equal("Entertainment", result[1]);
        }

        [Fact]
        public void SkipsNonNumericIds()
        {
            var result = FolderMappingParser.Parse("Sports=abc,2,xyz");
            Assert.Single(result);
            Assert.Equal("Sports", result[2]);
        }

        [Fact]
        public void HandlesWindowsLineEndings()
        {
            var result = FolderMappingParser.Parse("Sports=1\r\nNews=2\r\n");
            Assert.Equal("Sports", result[1]);
            Assert.Equal("News", result[2]);
        }

        [Fact]
        public void InterspersedComments()
        {
            var result = FolderMappingParser.Parse("# header\nSports=1\n# divider\nNews=2");
            Assert.Equal(2, result.Count);
            Assert.Equal("Sports", result[1]);
            Assert.Equal("News", result[2]);
        }

        [Fact]
        public void SingleCategoryId()
        {
            var result = FolderMappingParser.Parse("Sports=42");
            Assert.Single(result);
            Assert.Equal("Sports", result[42]);
        }
    }
}
