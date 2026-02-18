using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class UpdateCheckerTests
    {
        [Fact]
        public void NewerVersionAvailable()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.1.0", "https://github.com/release", "Bug fixes", "2025-01-01");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.0.0", result.CurrentVersion);
            Assert.Equal("1.1.0", result.LatestVersion);
            Assert.Equal("https://github.com/release", result.ReleaseUrl);
            Assert.Equal("Bug fixes", result.ReleaseNotes);
        }

        [Fact]
        public void SameVersionNotAvailable()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0", "https://github.com/release", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Equal("1.0.0", result.LatestVersion);
        }

        [Fact]
        public void OlderVersionNotAvailable()
        {
            var result = UpdateChecker.CompareVersions("2.0.0", "v1.5.0", "https://github.com/release", "", "");
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void StripLeadingV()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.2.3", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.2.3", result.LatestVersion);
        }

        [Fact]
        public void StripLeadingUpperV()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "V2.0.0", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("2.0.0", result.LatestVersion);
        }

        [Fact]
        public void TagWithoutPrefix()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "1.2.0", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.2.0", result.LatestVersion);
        }

        [Fact]
        public void MalformedTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "not-a-version", "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.False(string.IsNullOrEmpty(result.Error));
            Assert.Contains("Could not parse", result.Error);
        }

        [Fact]
        public void NullTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", null, "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Contains("No tag found", result.Error);
        }

        [Fact]
        public void EmptyTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "", "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Contains("No tag found", result.Error);
        }

        [Fact]
        public void ThreePartVersionComparison()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void FourPartVersionComparison()
        {
            var result = UpdateChecker.CompareVersions("1.0.0.0", "v1.0.0.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void MajorVersionBump()
        {
            var result = UpdateChecker.CompareVersions("1.9.9", "v2.0.0", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void CurrentVersionHigherMajor()
        {
            var result = UpdateChecker.CompareVersions("3.0.0", "v2.9.9", "", "", "");
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void PreservesReleaseUrlAndNotes()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0",
                "https://github.com/org/repo/releases/tag/v1.0.0",
                "Release notes here",
                "2025-06-15T10:00:00Z");
            Assert.Equal("https://github.com/org/repo/releases/tag/v1.0.0", result.ReleaseUrl);
            Assert.Equal("Release notes here", result.ReleaseNotes);
            Assert.Equal("2025-06-15T10:00:00Z", result.PublishedAt);
        }

        [Fact]
        public void NullReleaseFieldsDefaultToEmpty()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0", null, null, null);
            Assert.Equal("", result.ReleaseUrl);
            Assert.Equal("", result.ReleaseNotes);
            Assert.Equal("", result.PublishedAt);
        }

        [Fact]
        public void TwoPartVersionParsedCorrectly()
        {
            var result = UpdateChecker.CompareVersions("1.0", "v1.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        // ---- ExtractDllDownloadUrl tests ----

        [Fact]
        public void ExtractDllDownloadUrl_FindsCorrectAsset()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": [
                    {
                        ""name"": ""source.zip"",
                        ""browser_download_url"": ""https://github.com/example/source.zip""
                    },
                    {
                        ""name"": ""Emby.Xtream.Plugin.dll"",
                        ""browser_download_url"": ""https://github.com/example/Emby.Xtream.Plugin.dll""
                    },
                    {
                        ""name"": ""README.md"",
                        ""browser_download_url"": ""https://github.com/example/README.md""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Equal("https://github.com/example/Emby.Xtream.Plugin.dll", url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullWhenNoMatch()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": [
                    {
                        ""name"": ""source.zip"",
                        ""browser_download_url"": ""https://github.com/example/source.zip""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullWhenAssetsEmpty()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": []
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_CaseInsensitiveMatch()
        {
            var json = @"{
                ""assets"": [
                    {
                        ""name"": ""emby.xtream.plugin.dll"",
                        ""browser_download_url"": ""https://github.com/example/plugin.dll""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Equal("https://github.com/example/plugin.dll", url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullForNullJson()
        {
            var url = UpdateChecker.ExtractDllDownloadUrl(null, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullForNullAssetName()
        {
            var json = @"{ ""assets"": [] }";
            var url = UpdateChecker.ExtractDllDownloadUrl(json, null);
            Assert.Null(url);
        }
    }
}
