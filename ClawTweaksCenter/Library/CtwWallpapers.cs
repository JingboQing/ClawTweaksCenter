using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClawTweaksCenter.Library
{
    /// <summary>
    /// Backgrounds that ship with the project rather than with the build: a list and a set of PNGs
    /// in the Center repo, read over https.
    ///
    /// -- Why not in the exe -------------------------------------------------------------------
    /// Center is one self-contained 68 MB file that every user downloads in full, and a wallpaper is
    /// a megabyte and a half each. Putting them in the binary would charge everybody for pictures
    /// most of them never open, and - the part that actually decides it - adding one would then need
    /// a release. From the repo, adding a wallpaper is a commit to wallpapers\index.json and the
    /// file beside it: no build, no release, and it appears in every Center already installed.
    /// Exactly one picture is in the exe, and only because it has to work offline on the first
    /// start: see Core\DefaultBackground.cs.
    ///
    /// ⚠️ raw.githubusercontent caches for about four minutes. A wallpaper pushed and immediately
    /// looked for is not missing, it is early.
    /// </summary>
    public static class CtwWallpapers
    {
        public sealed class Wallpaper
        {
            public string Id { get; set; }
            /// <summary>NOT run through Loc.T anywhere: these are picture names, not UI copy.</summary>
            public string Name { get; set; }
            public string File { get; set; }
        }

        private sealed class IndexFile
        {
            public List<Wallpaper> Wallpapers { get; set; }
        }

        private const string BaseUrl =
            "https://raw.githubusercontent.com/enterTheVoidCode/ClawTweaksCenter/master/wallpapers/";

        /// <summary>Downloaded originals, kept so the second look at the gallery is instant and the
        /// third costs nothing. Deliberately NOT the art cache: what lands here is a copy of a
        /// published file, and only the copy the user actually picks becomes a background (it is
        /// copied on again, into the art cache, by the same path a picture of their own takes).</summary>
        public static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClawTweaks", "Center", "wallpapers");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClawTweaksCenter");
            return client;
        }

        /// <summary>
        /// The published list, or null when it could not be read.
        ///
        /// NULL AND EMPTY ARE DIFFERENT ANSWERS on purpose: "no network" and "the list is empty" want
        /// different words on screen, and a caller that cannot tell them apart writes the wrong one.
        /// Never cached to disk - the list is a few hundred bytes, and a stale one would hide a
        /// wallpaper that was added an hour ago.
        /// </summary>
        public static async Task<List<Wallpaper>> ListAsync(CancellationToken ct)
        {
            try
            {
                string json;
                using (var http = CreateClient())
                    json = await http.GetStringAsync(BaseUrl + "index.json", ct).ConfigureAwait(false);

                var parsed = JsonSerializer.Deserialize<IndexFile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

                var result = new List<Wallpaper>();
                if (parsed?.Wallpapers != null)
                {
                    foreach (var wp in parsed.Wallpapers)
                    {
                        // A row with no file is a typo in the index, and drawing it would be a tile
                        // that can never fill in. Skipped rather than shown as broken.
                        if (wp == null || string.IsNullOrWhiteSpace(wp.File)) continue;
                        if (string.IsNullOrWhiteSpace(wp.Name)) wp.Name = Path.GetFileNameWithoutExtension(wp.File);
                        result.Add(wp);
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Wallpapers] list could not be read: " + ex.Message);
                return null;
            }
        }

        /// <summary>The local copy of one wallpaper, downloading it the first time. Null when the
        /// download failed.</summary>
        public static async Task<string> EnsureFileAsync(Wallpaper wp, CancellationToken ct)
        {
            if (wp == null || string.IsNullOrWhiteSpace(wp.File)) return null;

            // The name from the index, never a path from it: a "file" of "..\\..\\something" would
            // otherwise write outside the cache. Path.GetFileName is what makes that harmless.
            string leaf = Path.GetFileName(wp.File);
            if (string.IsNullOrWhiteSpace(leaf)) return null;

            string target = Path.Combine(CacheDir, leaf);
            try { if (System.IO.File.Exists(target)) return target; }
            catch { }

            try
            {
                byte[] bytes;
                using (var http = CreateClient())
                    bytes = await http.GetByteArrayAsync(BaseUrl + Uri.EscapeDataString(leaf), ct).ConfigureAwait(false);

                Directory.CreateDirectory(CacheDir);
                // Written aside and moved into place: a download cut off halfway would otherwise
                // leave a truncated PNG under the right name, and every later visit would find that
                // file, decode nothing and never try again.
                string tmp = target + ".tmp";
                System.IO.File.WriteAllBytes(tmp, bytes);
                if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                System.IO.File.Move(tmp, target);
                return target;
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("[Wallpapers] download of " + leaf + " failed: " + ex.Message);
                return null;
            }
        }
    }
}
