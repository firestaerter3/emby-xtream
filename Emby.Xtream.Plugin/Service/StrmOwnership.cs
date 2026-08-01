using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Decides whether a file in the STRM library was written by this plugin.
    /// </summary>
    /// <remarks>
    /// Every delete path here matches by title or by "is a .strm", neither of which proves the
    /// plugin wrote the file. A user's own <c>Ben-Hur</c> folder, a hand-written <c>.nfo</c>, or a
    /// <c>trailer.strm</c> sitting beside plugin output all looked deletable.
    ///
    /// No manifest is needed to fix that: the plugin's own output already identifies itself. Every
    /// STRM it writes contains the provider URL it was built from
    /// (<c>{BaseUrl}/movie|series/{user}/{pass}/{id}.{ext}</c>), or a Dispatcharr proxy URL for
    /// multi-version entries. A STRM whose content starts with one of the configured hosts is ours.
    /// This needs no new state, no migration, and works on libraries that already exist.
    ///
    /// Everything here fails safe: anything not provably ours is treated as foreign and kept. The
    /// cost of a false negative is an orphan that survives; the cost of a false positive is
    /// destroyed user data.
    ///
    /// If <c>BaseUrl</c> changes, previously-written STRMs stop reading as owned and simply stop
    /// being cleaned. That is the safe direction, and ADR-004's naming-version resync already covers
    /// rewriting them.
    /// </remarks>
    internal static class StrmOwnership
    {
        /// <summary>The show-level NFO the plugin writes at a series root.</summary>
        private const string ShowNfoName = "tvshow.nfo";

        /// <summary>
        /// True when the STRM at <paramref name="path"/> was written by this plugin.
        /// </summary>
        /// <param name="path">Full path to a <c>.strm</c> file.</param>
        /// <param name="baseUrl">Configured Xtream server URL.</param>
        /// <param name="dispatcharrUrl">Configured Dispatcharr URL, if any.</param>
        public static bool IsOwnedStrm(string path, string baseUrl, string dispatcharrUrl)
        {
            try
            {
                var content = File.ReadAllText(path).Trim();
                if (content.Length == 0)
                {
                    return false;
                }

                return StartsWithHost(content, baseUrl) || StartsWithHost(content, dispatcharrUrl);
            }
            catch (Exception)
            {
                // Unreadable means not provably ours, so leave it alone.
                return false;
            }
        }

        private static bool StartsWithHost(string content, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var prefix = url.TrimEnd('/');
            if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Require a path boundary. A bare prefix match means a configured BaseUrl of
            // http://nas also claims a user's http://nas-backup/media/Movie.mkv, and this class
            // deletes what it claims.
            return content.Length == prefix.Length || content[prefix.Length] == '/';
        }

        /// <summary>
        /// True when the NFO at <paramref name="nfoPath"/> was written by this plugin.
        /// </summary>
        /// <remarks>
        /// The plugin writes exactly two NFO shapes: <c>{folderName}.nfo</c> beside the matching
        /// <c>{folderName}.strm</c> (movies), and <c>tvshow.nfo</c> at a series root. Anything else
        /// in the library — a user's <c>movie.nfo</c>, a hand-edited episode NFO — is foreign.
        /// </remarks>
        /// <param name="nfoPath">Full path to a <c>.nfo</c> file.</param>
        /// <param name="ownedStrmsInTree">Owned STRM paths found anywhere under the item folder.</param>
        private static bool IsOwnedNfo(string nfoPath, ICollection<string> ownedStrmsInTree)
        {
            if (ownedStrmsInTree == null || ownedStrmsInTree.Count == 0)
            {
                return false;
            }

            if (string.Equals(Path.GetFileName(nfoPath), ShowNfoName, StringComparison.OrdinalIgnoreCase))
            {
                // A tvshow.nfo only exists because we wrote episodes under it.
                return true;
            }

            var dir = Path.GetDirectoryName(nfoPath);
            var baseName = Path.GetFileNameWithoutExtension(nfoPath);

            return ownedStrmsInTree.Any(strm =>
                string.Equals(Path.GetDirectoryName(strm), dir, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(strm), baseName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Deletes every plugin-written <c>.strm</c> and <c>.nfo</c> under <paramref name="dir"/>,
        /// then prunes whatever that emptied. Files the plugin did not write stay, and so does the
        /// folder if it still holds any of them.
        /// </summary>
        /// <param name="dir">Item folder to clear.</param>
        /// <param name="baseUrl">Configured Xtream server URL.</param>
        /// <param name="dispatcharrUrl">Configured Dispatcharr URL, if any.</param>
        /// <param name="folderRemoved">True when <paramref name="dir"/> itself was pruned away.</param>
        /// <returns>Number of files deleted.</returns>
        public static int DeleteOwnedFiles(string dir, string baseUrl, string dispatcharrUrl, out bool folderRemoved)
        {
            folderRemoved = false;

            if (!Directory.Exists(dir))
            {
                folderRemoved = true;
                return 0;
            }

            List<string> ownedStrms;
            List<string> nfos;
            try
            {
                // Resolve ownership across the whole tree BEFORE deleting anything, because the
                // NFO rule is defined in terms of the STRM files that were there.
                ownedStrms = Directory
                    .GetFiles(dir, "*.strm", SearchOption.AllDirectories)
                    .Where(f => IsOwnedStrm(f, baseUrl, dispatcharrUrl))
                    .ToList();
                nfos = Directory.GetFiles(dir, "*.nfo", SearchOption.AllDirectories).ToList();
            }
            catch (Exception)
            {
                // An access error or a filesystem race must not escape. The delete-all endpoint
                // wraps its whole folder loop in one try/catch, so letting this out would stop
                // cleanup for every remaining folder. Retaining this one folder is the safe
                // outcome, and it is reported as kept rather than removed.
                return 0;
            }

            if (ownedStrms.Count == 0)
            {
                // Nothing here is ours. Matching is by title alone, so this is the case where a
                // user's own folder happens to share a name with a provider title.
                return 0;
            }

            var deleted = 0;

            foreach (var strm in ownedStrms)
            {
                if (TryDelete(strm))
                {
                    deleted++;
                }
            }

            foreach (var nfo in nfos)
            {
                if (IsOwnedNfo(nfo, ownedStrms) && TryDelete(nfo))
                {
                    deleted++;
                }
            }

            folderRemoved = TryPruneEmptyTree(dir);
            return deleted;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes <paramref name="dir"/> and any subdirectory of it that is empty, deepest first.
        /// Stops at the first level that still holds something.
        /// </summary>
        /// <param name="dir">The directory to prune.</param>
        /// <returns>True if <paramref name="dir"/> itself was removed.</returns>
        private static bool TryPruneEmptyTree(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }

            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    TryPruneEmptyTree(sub);
                }

                if (Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    return true;
                }
            }
            catch (Exception)
            {
                // A file can appear between the emptiness check and the delete, or permissions can
                // block it. The Try prefix promises this does not throw, and the delete-all endpoint
                // wraps its whole folder loop in one try/catch — letting this escape would abort the
                // remaining folders and report the entire operation as failed.
            }

            return false;
        }
    }
}
