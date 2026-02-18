using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class LogSanitizerTests
    {
        [Fact]
        public void RedactsIpAddresses()
        {
            var result = LogSanitizer.SanitizeLine(
                "Connected to 192.168.1.100 on port 8080", "", "", "", "");
            Assert.Equal("Connected to <ip-redacted> on port 8080", result);
        }

        [Fact]
        public void RedactsMultipleIps()
        {
            var result = LogSanitizer.SanitizeLine(
                "From 10.0.0.1 to 172.16.0.1", "", "", "", "");
            Assert.Equal("From <ip-redacted> to <ip-redacted>", result);
        }

        [Fact]
        public void RedactsConfiguredUsername()
        {
            var result = LogSanitizer.SanitizeLine(
                "Login as myuser succeeded", "myuser", "mypass", "", "");
            Assert.Equal("Login as <redacted> succeeded", result);
        }

        [Fact]
        public void RedactsConfiguredPassword()
        {
            var result = LogSanitizer.SanitizeLine(
                "Using password s3cret123", "", "s3cret123", "", "");
            Assert.Equal("Using password <redacted>", result);
        }

        [Fact]
        public void RedactsDispatcharrCredentials()
        {
            var result = LogSanitizer.SanitizeLine(
                "Dispatcharr user=admin pass=hunter2",
                "", "", "admin", "hunter2");
            Assert.Equal("Dispatcharr user=<redacted> pass=<redacted>", result);
        }

        [Fact]
        public void RedactsEmailAddresses()
        {
            var result = LogSanitizer.SanitizeLine(
                "User email is user@example.com logged in", "", "", "", "");
            Assert.Equal("User email is <email-redacted> logged in", result);
        }

        [Fact]
        public void RedactsXtreamUrlCredentials()
        {
            var result = LogSanitizer.SanitizeLine(
                "Stream URL: http://example.com/live/john/pass123/12345.ts", "", "", "", "");
            Assert.Contains("/live/<user>/<pass>/", result);
            Assert.DoesNotContain("john", result);
            Assert.DoesNotContain("pass123", result);
        }

        [Fact]
        public void RedactsProviderHostname()
        {
            var result = LogSanitizer.SanitizeLine(
                "Fetching http://myprovider.com:8080/player_api.php?action=get_live", "", "", "", "");
            Assert.Contains("<provider-host>", result);
            Assert.DoesNotContain("myprovider.com", result);
        }

        [Fact]
        public void NoFalsePositivesOnNormalText()
        {
            const string line = "XtreamTuner loaded 150 channels successfully";
            var result = LogSanitizer.SanitizeLine(line, "", "", "", "");
            Assert.Equal(line, result);
        }

        [Fact]
        public void EmptyCredentialsSkipped()
        {
            const string line = "Normal log line with no secrets";
            var result = LogSanitizer.SanitizeLine(line, "", "", null, null);
            Assert.Equal(line, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void HandlesNullOrEmptyLine(string input)
        {
            Assert.Equal(input, LogSanitizer.SanitizeLine(input, "user", "pass", "du", "dp"));
        }

        [Fact]
        public void RedactsProviderHostWithoutPort()
        {
            var result = LogSanitizer.SanitizeLine(
                "Fetching http://myprovider.com/player_api.php?action=get_live", "", "", "", "");
            Assert.Contains("<provider-host>", result);
            Assert.DoesNotContain("myprovider.com", result);
        }

        [Theory]
        [InlineData("http://host.com/movie/123.mp4", "/movie/")]
        [InlineData("http://host.com/series/456.mp4", "/series/")]
        public void RedactsMovieAndSeriesUrlPaths(string input, string pathKept)
        {
            var result = LogSanitizer.SanitizeLine(input, "", "", "", "");
            Assert.Contains("<provider-host>", result);
            Assert.Contains(pathKept, result);
            Assert.DoesNotContain("host.com", result);
        }

        [Fact]
        public void RedactsHttpsUrls()
        {
            var result = LogSanitizer.SanitizeLine(
                "Fetching https://secure.provider.com:443/live/user1/pass1/123.ts", "", "", "", "");
            Assert.Contains("<provider-host>", result);
            Assert.DoesNotContain("secure.provider.com", result);
        }

        [Fact]
        public void MultiplePiiTypesInOneLine()
        {
            var result = LogSanitizer.SanitizeLine(
                "User myuser at 10.0.0.1 sent email test@example.com via http://host.com/live/u/p/1.ts",
                "myuser", "mypass", "", "");
            Assert.DoesNotContain("myuser", result);
            Assert.DoesNotContain("10.0.0.1", result);
            Assert.DoesNotContain("test@example.com", result);
            Assert.DoesNotContain("host.com", result);
        }

        [Fact]
        public void CredentialSubstringReplacesPartialWords()
        {
            // string.Replace matches substrings — documents this known behavior
            var result = LogSanitizer.SanitizeLine(
                "the password field is empty", "", "pass", "", "");
            Assert.Equal("the <redacted>word field is empty", result);
        }
    }
}
