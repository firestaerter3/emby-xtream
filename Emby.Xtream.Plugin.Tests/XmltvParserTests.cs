using System.Collections.Generic;
using System.IO;
using System.Text;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class XmltvParserTests
    {
        private static Dictionary<string, List<EpgProgram>> Parse(string xml)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
                return XmltvParser.Parse(stream, null, null);
        }

        [Fact]
        public void ParseProgramme_WithIcon_SetsImageUrl()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Test Show</title>
    <icon src=""https://example.com/poster.jpg"" />
  </programme>
</tv>";

            var result = Parse(xml);

            Assert.True(result.ContainsKey("ch1"));
            var prog = Assert.Single(result["ch1"]);
            Assert.Equal("https://example.com/poster.jpg", prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_WithoutIcon_ImageUrlIsNull()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Test Show</title>
  </programme>
</tv>";

            var result = Parse(xml);

            Assert.True(result.ContainsKey("ch1"));
            var prog = Assert.Single(result["ch1"]);
            Assert.Null(prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_IconWithEmptySrc_ImageUrlIsNull()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch1"">
    <title>Test Show</title>
    <icon src="""" />
  </programme>
</tv>";

            var result = Parse(xml);

            Assert.True(result.ContainsKey("ch1"));
            var prog = Assert.Single(result["ch1"]);
            Assert.Null(prog.ImageUrl);
        }

        [Fact]
        public void ParseProgramme_WithTitleDescriptionAndIcon_ParsesAll()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<tv>
  <programme start=""20250101120000 +0000"" stop=""20250101130000 +0000"" channel=""ch2"">
    <title>Documentary Night</title>
    <desc>A fascinating documentary.</desc>
    <icon src=""https://cdn.example.com/doc-thumb.png"" />
  </programme>
</tv>";

            var result = Parse(xml);

            Assert.True(result.ContainsKey("ch2"));
            var prog = Assert.Single(result["ch2"]);
            Assert.Equal("Documentary Night", prog.Title);
            Assert.Equal("A fascinating documentary.", prog.Description);
            Assert.Equal("https://cdn.example.com/doc-thumb.png", prog.ImageUrl);
        }
    }
}
