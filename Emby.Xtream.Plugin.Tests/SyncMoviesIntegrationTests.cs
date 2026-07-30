using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Integration tests for <see cref="StrmSyncService.SyncMoviesAsync"/>.
    ///
    /// Path structure (MovieFolderMode = "single"):
    ///   {StrmLibraryPath}/Movies/{folderName}/{folderName}.strm
    ///
    /// URL pattern (no selected categories):
    ///   ...player_api.php?...&amp;action=get_vod_streams
    /// </summary>
    public class SyncMoviesIntegrationTests : SyncTestBase
    {
        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Compute the expected STRM path for a movie with a plain name
        /// (no TMDB ID, so folderName == sanitizedName).
        /// </summary>
        private string MovieStrmPath(string movieName)
        {
            var folderName = movieName; // SanitizeFileName for plain ASCII names is identity
            return Path.Combine(TempDir.Path, "Movies", folderName, folderName + ".strm");
        }

        /// <summary>
        /// Register a successful get_vod_streams response.
        /// </summary>
        private void RegisterVodStreams(string json)
            => Handler.RespondWith("get_vod_streams", json);

        // -----------------------------------------------------------------
        // Test 1: HappyPath_WritesStrmFile
        // -----------------------------------------------------------------

        [Fact]
        public async Task HappyPath_WritesStrmFile()
        {
            var config = DefaultConfig();
            var json = VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var strmPath = MovieStrmPath("Test Movie");
            Assert.True(File.Exists(strmPath), $"Expected STRM file at: {strmPath}");
            Assert.Equal(1, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Test 2: SmartSkip_ExistingFile_NotRewritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task SmartSkip_ExistingFile_NotRewritten()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastMovieSyncTimestamp = 9999; // movie.Added (5000) < 9999 → existing

            var strmPath = MovieStrmPath("Test Movie");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            // added=5000 < lastTs=9999 → treated as existing, should be skipped
            var json = VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 5000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal("SENTINEL", File.ReadAllText(strmPath));
        }

        // -----------------------------------------------------------------
        // Test 3: NamingVersionUpgrade_BypassesSmartSkip_OverwritesSentinel
        // -----------------------------------------------------------------

        [Fact]
        public async Task NamingVersionUpgrade_BypassesSmartSkip_OverwritesSentinel()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastMovieSyncTimestamp = 9999;
            config.StrmNamingVersion = 0; // stale version → triggers upgrade → resets timestamps

            var strmPath = MovieStrmPath("Test Movie");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            // After upgrade, LastMovieSyncTimestamp is reset to 0, so movie is treated as new
            var json = VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 5000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var content = File.ReadAllText(strmPath);
            Assert.NotEqual("SENTINEL", content);
            // At least 2 saves: one from naming-version upgrade, one from timestamp update
            Assert.True(SaveConfigCallCount >= 2, $"Expected >= 2 saves, got {SaveConfigCallCount}");
        }

        // -----------------------------------------------------------------
        // Test 4: AddedZero_AllStreams_FileStillWrittenWhenNoSmartSkip
        // -----------------------------------------------------------------

        [Fact]
        public async Task AddedZero_AllStreams_FileStillWrittenWhenNoSmartSkip()
        {
            // SmartSkipExisting = false (default) → always write even if added==0
            // LastMovieSyncTimestamp = 100 and movie.Added = 0 → 0 is NOT > 100 → no timestamp save
            var config = DefaultConfig();
            config.LastMovieSyncTimestamp = 100;
            config.SmartSkipExisting = false;

            var json = VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 0));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var strmPath = MovieStrmPath("Test Movie");
            Assert.True(File.Exists(strmPath), $"Expected STRM file at: {strmPath}");
            // maxAdded (0) is NOT > LastMovieSyncTimestamp (100) → saveConfig not called for timestamp
            Assert.Equal(0, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Test 5: OrphanCleanup_RemovesStaleFile
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanCleanup_RemovesStaleFile()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            // Pre-write an orphan for "Old Movie"
            var orphanPath = MovieStrmPath("Old Movie");
            Directory.CreateDirectory(Path.GetDirectoryName(orphanPath));
            File.WriteAllText(orphanPath, "orphan");

            // Provider returns a different movie only
            var json = VodStreamsJson(VodStream(streamId: 2, name: "New Movie", added: 1000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(orphanPath), "Orphan STRM file should have been deleted");
            Assert.True(File.Exists(MovieStrmPath("New Movie")));
        }

        // -----------------------------------------------------------------
        // Test 6: OrphanThreshold_AboveThreshold_CleanupSkipped
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanThreshold_AboveThreshold_CleanupSkipped()
        {
            // Threshold fires when: existingStrms.Length > 10 AND orphanRatio > safetyThreshold
            // Write 12 existing STRM files; provider returns only 1 movie → 11/12 orphaned (91.7%)
            // safetyThreshold = 0.5 → 91.7% > 50% → cleanup skipped
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.5;

            var moviesRoot = Path.Combine(TempDir.Path, "Movies");

            // Write 12 pre-existing STRM files for movies 1–12
            for (int i = 1; i <= 12; i++)
            {
                var name = $"Movie {i:D2}";
                var dir = Path.Combine(moviesRoot, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name + ".strm"), $"stream {i}");
            }

            // Provider returns only movie 1 → 11 orphans out of 12 = 91.7%
            var json = VodStreamsJson(VodStream(streamId: 1, name: "Movie 01", added: 1000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            // All 12 files should be preserved (threshold blocked cleanup)
            var remaining = Directory.GetFiles(moviesRoot, "*.strm", SearchOption.AllDirectories);
            Assert.Equal(12, remaining.Length);
        }

        // -----------------------------------------------------------------
        // Test 7: OrphanThreshold_BelowThreshold_CleanupProceeds
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanThreshold_BelowThreshold_CleanupProceeds()
        {
            // Write 12 existing STRM files; provider returns 10 movies → 2/12 orphaned (16.7%)
            // safetyThreshold = 0.5 → 16.7% < 50% → cleanup proceeds
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.5;

            var moviesRoot = Path.Combine(TempDir.Path, "Movies");

            for (int i = 1; i <= 12; i++)
            {
                var name = $"Movie {i:D2}";
                var dir = Path.Combine(moviesRoot, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name + ".strm"), $"stream {i}");
            }

            // Provider returns movies 1–10 → movies 11 and 12 become orphans
            var streams = new object[10];
            for (int i = 0; i < 10; i++)
            {
                streams[i] = VodStream(streamId: i + 1, name: $"Movie {(i + 1):D2}", added: 1000);
            }
            RegisterVodStreams(VodStreamsJson(streams));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var remaining = Directory.GetFiles(moviesRoot, "*.strm", SearchOption.AllDirectories);
            Assert.Equal(10, remaining.Length);
        }

        // -----------------------------------------------------------------
        // Test 8: HttpError_SyncThrows
        // -----------------------------------------------------------------

        [Fact]
        public async Task HttpError_SyncThrows()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams", "Service Unavailable", HttpStatusCode.ServiceUnavailable);

            await Assert.ThrowsAnyAsync<Exception>(
                () => MakeService().SyncMoviesAsync(config, None, SaveConfig))
                ;
        }

        // -----------------------------------------------------------------
        // Test 9: EmptyResponse_NoFilesWritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task EmptyResponse_NoFilesWritten()
        {
            var config = DefaultConfig();
            RegisterVodStreams("[]");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var moviesRoot = Path.Combine(TempDir.Path, "Movies");
            var files = Directory.Exists(moviesRoot)
                ? Directory.GetFiles(moviesRoot, "*.strm", SearchOption.AllDirectories)
                : Array.Empty<string>();

            Assert.Empty(files);
            Assert.Equal(0, SaveConfigCallCount);
        }

        /// <summary>
        /// Multiple Folders (<c>custom</c>) with no category→folder mappings used to fetch every VOD
        /// stream then skip each one — confusing UX. Abort early with no HTTP calls.
        /// </summary>
        [Fact]
        public async Task CustomMode_EmptyMappings_AbortsWithoutHttp()
        {
            var config = DefaultConfig();
            config.MovieFolderMode = "custom";
            config.MovieFolderMappings = string.Empty;

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Empty(Handler.ReceivedUrls);
            Assert.False(string.IsNullOrEmpty(svc.MovieProgress.AbortReason));
            Assert.Equal(0, svc.MovieProgress.Total);
            Assert.Equal(0, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Per-item exclusion (issue #57)
        // -----------------------------------------------------------------

        [Fact]
        public async Task ExcludedMovie_NotWritten_OthersUnaffected()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Keep Me")));
            Assert.False(File.Exists(MovieStrmPath("Drop Me")));
        }

        [Fact]
        public async Task ExcludedMovie_ExistingFolderDeleted_WithoutOrphanCleanup()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = false;
            config.ExcludedVodStreamIds = new[] { 2 };

            // Simulate a previous sync having written "Drop Me"
            var staleStrm = MovieStrmPath("Drop Me");
            Directory.CreateDirectory(Path.GetDirectoryName(staleStrm));
            File.WriteAllText(staleStrm, "http://fake-xtream/movie/user/pass/2.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Movies", "Drop Me")));
            Assert.True(File.Exists(MovieStrmPath("Keep Me")));
        }

        [Fact]
        public async Task ExcludedMovie_FolderWithTmdbSuffix_Deleted()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };

            var staleDir = Path.Combine(TempDir.Path, "Movies", "Drop Me [tmdbid=550]");
            Directory.CreateDirectory(staleDir);
            File.WriteAllText(Path.Combine(staleDir, "Drop Me [tmdbid=550].strm"), "http://old");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(staleDir));
        }

        [Fact]
        public async Task ExcludedMovie_DoesNotStallDeltaWatermark()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 5000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(5000, config.LastMovieSyncTimestamp);
        }
    }
}
