using System.IO;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for content-based ownership verification (ADR-014).
    ///
    /// Both delete paths matched by title or by "is a .strm", neither of which proves the plugin
    /// wrote the file. These tests seed files the plugin did NOT write and assert they survive.
    /// </summary>
    public class StrmOwnershipTests : SyncTestBase
    {
        private const string OwnedMovieUrl = "http://fake-xtream/movie/user/pass/42.mkv";
        private const string ForeignUrl = "http://my-own-nas/media/Movie.mkv";

        private string MovieDir(string name) => Path.Combine(TempDir.Path, "Movies", name);

        private string WriteFile(string dir, string fileName, string content)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        // -----------------------------------------------------------------
        // Unit: IsOwnedStrm
        // -----------------------------------------------------------------

        [Fact]
        public void IsOwnedStrm_ProviderUrl_IsOwned()
        {
            var path = WriteFile(MovieDir("A"), "A.strm", OwnedMovieUrl);
            Assert.True(StrmOwnership.IsOwnedStrm(path, "http://fake-xtream", null));
        }

        [Fact]
        public void IsOwnedStrm_TrailingSlashOnBaseUrl_StillOwned()
        {
            var path = WriteFile(MovieDir("A"), "A.strm", OwnedMovieUrl);
            Assert.True(StrmOwnership.IsOwnedStrm(path, "http://fake-xtream/", null));
        }

        [Fact]
        public void IsOwnedStrm_DispatcharrProxyUrl_IsOwned()
        {
            // Multi-version entries point at Dispatcharr, not at BaseUrl. Checking only BaseUrl
            // would classify every multi-version STRM as foreign and stop cleaning them.
            var path = WriteFile(MovieDir("A"), "A - Version 2.strm",
                "http://dispatcharr:9191/proxy/vod/movie/abc-uuid?stream_id=7");
            Assert.True(StrmOwnership.IsOwnedStrm(path, "http://fake-xtream", "http://dispatcharr:9191"));
        }

        [Fact]
        public void IsOwnedStrm_ForeignUrl_IsNotOwned()
        {
            var path = WriteFile(MovieDir("A"), "A.strm", ForeignUrl);
            Assert.False(StrmOwnership.IsOwnedStrm(path, "http://fake-xtream", null));
        }

        [Fact]
        public void IsOwnedStrm_HostPrefixWithoutPathBoundary_IsNotOwned()
        {
            // A bare prefix match means a BaseUrl of http://nas also claims the user's
            // http://nas-backup/... file, and this class deletes what it claims.
            var path = WriteFile(MovieDir("A"), "A.strm", "http://nas-backup/media/Movie.mkv");
            Assert.False(StrmOwnership.IsOwnedStrm(path, "http://nas", null));
        }

        [Fact]
        public void IsOwnedStrm_HostPrefixWithPathBoundary_IsOwned()
        {
            var path = WriteFile(MovieDir("B"), "B.strm", "http://nas/movie/user/pass/1.mkv");
            Assert.True(StrmOwnership.IsOwnedStrm(path, "http://nas", null));
        }

        [Fact]
        public void IsOwnedStrm_EmptyFile_IsNotOwned()
        {
            var path = WriteFile(MovieDir("A"), "A.strm", "");
            Assert.False(StrmOwnership.IsOwnedStrm(path, "http://fake-xtream", null));
        }

        [Fact]
        public void IsOwnedStrm_MissingFile_IsNotOwned()
        {
            var path = Path.Combine(MovieDir("Nope"), "Nope.strm");
            Assert.False(StrmOwnership.IsOwnedStrm(path, "http://fake-xtream", null));
        }

        // -----------------------------------------------------------------
        // Unit: DeleteOwnedFiles
        // -----------------------------------------------------------------

        [Fact]
        public void DeleteOwnedFiles_LeavesForeignStrmAndNfo()
        {
            var dir = MovieDir("Mixed");
            var ours = WriteFile(dir, "Mixed.strm", OwnedMovieUrl);
            var ourNfo = WriteFile(dir, "Mixed.nfo", "<movie />");
            var theirStrm = WriteFile(dir, "trailer.strm", ForeignUrl);
            var theirNfo = WriteFile(dir, "my-notes.nfo", "hand written");

            bool folderRemoved;
            var deleted = StrmOwnership.DeleteOwnedFiles(dir, "http://fake-xtream", null, out folderRemoved);

            Assert.Equal(2, deleted);
            Assert.False(File.Exists(ours));
            Assert.False(File.Exists(ourNfo));
            Assert.True(File.Exists(theirStrm), "A user's own STRM beside our output must survive");
            Assert.True(File.Exists(theirNfo), "A hand-written NFO must survive");
            Assert.False(folderRemoved, "The folder still holds the user's files");
        }

        [Fact]
        public void DeleteOwnedFiles_NothingOwned_DeletesNothing()
        {
            var dir = MovieDir("Ben-Hur");
            var theirs = WriteFile(dir, "Ben-Hur.strm", ForeignUrl);

            bool folderRemoved;
            var deleted = StrmOwnership.DeleteOwnedFiles(dir, "http://fake-xtream", null, out folderRemoved);

            Assert.Equal(0, deleted);
            Assert.True(File.Exists(theirs));
        }

        [Fact]
        public void DeleteOwnedFiles_AllOwned_PrunesFolder()
        {
            var dir = MovieDir("Ours");
            WriteFile(dir, "Ours.strm", OwnedMovieUrl);
            WriteFile(dir, "Ours.nfo", "<movie />");

            bool folderRemoved;
            var deleted = StrmOwnership.DeleteOwnedFiles(dir, "http://fake-xtream", null, out folderRemoved);

            Assert.Equal(2, deleted);
            Assert.True(folderRemoved);
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void DeleteOwnedFiles_ShowNfoGoesWithOwnedEpisodes()
        {
            var showDir = Path.Combine(TempDir.Path, "Shows", "Our Show");
            var showNfo = WriteFile(showDir, "tvshow.nfo", "<tvshow />");
            var episode = WriteFile(Path.Combine(showDir, "Season 01"), "S01E01.strm",
                "http://fake-xtream/series/user/pass/5.mp4");

            bool folderRemoved;
            var deleted = StrmOwnership.DeleteOwnedFiles(showDir, "http://fake-xtream", null, out folderRemoved);

            Assert.Equal(2, deleted);
            Assert.False(File.Exists(showNfo));
            Assert.False(File.Exists(episode));
            Assert.True(folderRemoved);
        }

        // -----------------------------------------------------------------
        // Integration: orphan cleanup
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanCleanup_LeavesUserOwnedStrm()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            // A STRM the user maintains themselves, pointing at their own NAS. The provider has
            // never heard of it, so by path-difference alone it looks exactly like an orphan.
            var userStrm = WriteFile(MovieDir("My Home Video"), "My Home Video.strm", ForeignUrl);

            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 1, name: "Provider Movie", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(userStrm),
                "A STRM this plugin did not write must never be deleted as an orphan");
        }

        [Fact]
        public async Task OrphanCleanup_StillRemovesOurOwnStaleFile()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            var ourStale = WriteFile(MovieDir("Old Movie"), "Old Movie.strm", OwnedMovieUrl);

            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 1, name: "New Movie", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(ourStale),
                "A stale file this plugin did write is still a real orphan");
        }

        // -----------------------------------------------------------------
        // Integration: per-item exclusion
        // -----------------------------------------------------------------

        [Fact]
        public async Task ExcludedMovie_LeavesUserFilesInSameFolder()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = false;
            config.ExcludedVodStreamIds = new[] { 7 };

            // Our output plus files the user added alongside it.
            var dir = MovieDir("Drop Me");
            var ourStrm = WriteFile(dir, "Drop Me.strm", OwnedMovieUrl);
            var userTrailer = WriteFile(dir, "trailer.strm", ForeignUrl);
            var userNfo = WriteFile(dir, "my-notes.nfo", "hand written");

            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 7, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(ourStrm), "The excluded title's own STRM should go");
            Assert.True(File.Exists(userTrailer), "A user's trailer.strm in the same folder must survive");
            Assert.True(File.Exists(userNfo), "A user's hand-written NFO must survive");
        }

        [Fact]
        public async Task ExcludedMovie_UserFolderWithSameTitleUntouched()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = false;
            config.ExcludedVodStreamIds = new[] { 7 };

            // The user's own copy, which merely shares a title with the provider's.
            var userStrm = WriteFile(MovieDir("Ben-Hur"), "Ben-Hur.strm", ForeignUrl);

            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 7, name: "Ben-Hur", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(userStrm),
                "Matching is by title, so a title match is not proof of ownership");
        }
    }
}
