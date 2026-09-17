using System;
using System.IO;
using System.Windows;
using ClawTweaksCenter.Library;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// The picture Center starts life with.
    ///
    /// -- Why this is a one-shot SEED and not a fallback ----------------------------------------
    /// The obvious shape - "nothing stored, so draw the packaged one" - cannot be undone. It would
    /// put the picture back on every single start for anyone who chose "No background", and nothing
    /// on screen would say why. So the file is copied into the art cache ONCE and is an ordinary
    /// stored background from that moment on: removable, replaceable, and retired by the same
    /// DeleteCachedBackground as any other one - which is why it carries the "background_" prefix.
    ///
    /// Somebody who already has a background keeps it. The stamp still moves in that case, so they
    /// are never asked again either.
    /// </summary>
    internal static class DefaultBackground
    {
        /// <summary>Raise this AND ship a new Assets\wallpapers\ctw-default.png to hand out a
        /// different default. It still only reaches people whose background is empty at that
        /// moment - a picture somebody chose is never overwritten.</summary>
        private const int SeedVersion = 1;

        private const string PackUri = "pack://application:,,,/Assets/wallpapers/ctw-default.png";
        private const string FileName = "background_ctw_default_v1.png";

        /// <summary>Call before the first ApplyBackgroundImage - after it, the seeded picture would
        /// only appear on the next start.</summary>
        public static void SeedOnce()
        {
            try
            {
                if (CenterSettings.BackgroundSeedVersion >= SeedVersion) return;

                // The stamp FIRST, and it is written even if the copy below fails. A seed that
                // retries is a seed that runs again on a machine where the user has since said no;
                // the price of never retrying is one start without a picture.
                CenterSettings.BackgroundSeedVersion = SeedVersion;

                if (!string.IsNullOrEmpty(CenterSettings.BackgroundImagePath))
                {
                    InstallLog.Write("[Background] default skipped - a background is already set");
                    return;
                }

                var res = Application.GetResourceStream(new Uri(PackUri, UriKind.Absolute));
                if (res == null)
                {
                    InstallLog.Write("[Background] the packaged default is missing from this build");
                    return;
                }

                Directory.CreateDirectory(SteamGridDb.CacheDir);
                string target = Path.Combine(SteamGridDb.CacheDir, FileName);
                using (var stream = res.Stream)
                using (var file = File.Create(target))
                    stream.CopyTo(file);

                CenterSettings.BackgroundImagePath = target;
                InstallLog.Write("[Background] default applied: " + target);
            }
            catch (Exception ex)
            {
                InstallLog.Write("[Background] default seed failed: " + ex.Message);
            }
        }
    }
}
