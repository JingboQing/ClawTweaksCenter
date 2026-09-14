using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Asks the helper's scheduled task to run, EARLY, when Center is the Windows full-screen
    /// experience (FSE) start app.
    ///
    /// WHY THIS EXISTS. In FSE the order is upside down: Center is up and the library is drawn
    /// while the controller mount is still pending, so the virtual pad appears in the middle of the
    /// user's first navigation (reported 2026-09-14). The helper's task is a LogonTrigger with zero
    /// delay — nothing of OURS delays it; Windows simply gets to it late among everything else
    /// queued at logon. Center, launched by the shell as the gaming home app, is demonstrably there
    /// first, so it is the one thing on the machine that can say "now" earlier than Windows does.
    ///
    /// It also helps a second problem by accident: with the helper started before Center opens the
    /// library, Steam (which Center may prewarm there) no longer gets ahead of the mount — the race
    /// in Doku/CASE_DoubleInput_Claw8EX_Phantom_XUSB.md §9.
    ///
    /// ⚠️ FSE ONLY, AND THAT IS NOT A CONVENIENCE. Outside FSE the dependency runs the OTHER way:
    /// the helper is what brings Center up. Starting the helper from a desktop-launched Center would
    /// invert an orchestration that already works and risk two startup sequences racing.
    ///
    /// NOTHING HERE IS NEW MACHINERY. HelperControl.RunScheduledTask() is the same call the helper's
    /// own ElevationBootstrapper makes, and the widget already launches the helper this way. Center
    /// stays unelevated throughout; the task carries the elevation, as it always has.
    /// </summary>
    internal static class FseHelperStart
    {
        private const string PackageFamily = "MSIClaw.ClawTweaks_7eszav2039cvc";

        /// <summary>Where Windows records the user's gaming-home choice. We only ever READ it.</summary>
        private const string GamingConfigKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";

        /// <summary>Our FSE package family — the value is "&lt;family&gt;!App" (an AUMID).</summary>
        private const string FsePackagePrefix = "MSIClaw.ClawTweaksCenterFSE_";

        /// <summary>
        /// Marker so a second look does not fire a second start. Lives beside the handover files, in
        /// LocalCache\Local — writable from both integrity levels and it survives a package swap.
        /// </summary>
        private const string RequestFileName = "helper-start-request.txt";

        /// <summary>
        /// Older than this and the marker is debris, not an instruction — the same boot-loop guard
        /// HelperHandover uses, and for the same reason: a stale file must never be able to suppress
        /// a start forever.
        /// </summary>
        private static readonly TimeSpan RequestMaxAge = TimeSpan.FromSeconds(60);

        /// <summary>
        /// True when BOTH conditions hold that make Center the thing Windows boots into:
        /// the FSE package is the chosen gaming home app, AND Windows is set to start into it.
        /// Either alone is not enough — a registered home app that Windows does not boot into
        /// leaves us an ordinary desktop app, and a machine set to boot into someone ELSE's home
        /// app is not ours to speed up.
        /// </summary>
        internal static bool IsFseStartApp(out string detail)
        {
            detail = "";
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(GamingConfigKey))
                {
                    if (key == null) { detail = "GamingConfiguration missing"; return false; }

                    string homeApp = key.GetValue("GamingHomeApp") as string ?? "";
                    object startupRaw = key.GetValue("StartupToGamingHome");
                    int startup = 0;
                    if (startupRaw != null)
                        int.TryParse(Convert.ToString(startupRaw, CultureInfo.InvariantCulture), out startup);

                    bool isOurs = homeApp.StartsWith(FsePackagePrefix, StringComparison.OrdinalIgnoreCase);
                    bool bootsIntoIt = startup == 1;

                    detail = $"GamingHomeApp='{homeApp}' ours={isOurs}, StartupToGamingHome={startup}";
                    return isOurs && bootsIntoIt;
                }
            }
            catch (Exception ex)
            {
                detail = $"read threw: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Fire-and-forget: bring the helper up now, if all guards agree. Returns what happened so the
        /// caller can log ONE line — this runs before any window exists, so there is nowhere to show
        /// anything and nothing here is worth bothering the user with.
        ///
        /// Guard order is deliberate, cheapest and most decisive first:
        ///   1. not the FSE start app  → not our job (see the class remarks)
        ///   2. a helper is already running → nothing to do
        ///   3. a recent request exists  → someone already asked; asking again is how you get two
        ///   4. no scheduled task        → the helper has not set itself up yet. Center must NOT
        ///                                 create it: that path is the helper's, deliberately, and
        ///                                 it owns the single UAC prompt that comes with it.
        /// </summary>
        internal static string TryStartHelper()
        {
            if (!IsFseStartApp(out string fseDetail))
                return $"skipped: not the FSE start app ({fseDetail})";

            if (HelperControl.HelperRunning())
                return "skipped: helper already running";

            string folder = Shared.IPC.HelperHandover.ResolveFolder(PackageFamily);
            string requestPath = Path.Combine(folder, RequestFileName);

            if (TryReadRecentRequest(requestPath, out TimeSpan age))
                return $"skipped: a start was already requested {age.TotalSeconds:F0}s ago";

            if (!HelperControl.ScheduledTaskExists())
                return "skipped: no scheduled task yet (the helper registers it itself)";

            WriteRequest(requestPath);

            bool ok = HelperControl.RunScheduledTask();
            return ok ? "requested: scheduled task run" : "FAILED: schtasks /Run did not succeed";
        }

        /// <summary>
        /// True if a marker exists and is younger than <see cref="RequestMaxAge"/>. An unreadable or
        /// unparsable marker counts as ABSENT: the failure mode we want is one extra start attempt,
        /// which the task's own IgnoreNew policy absorbs, not a helper that never starts because a
        /// corrupt file said someone else had it covered.
        /// </summary>
        private static bool TryReadRecentRequest(string path, out TimeSpan age)
        {
            age = TimeSpan.Zero;
            try
            {
                if (!File.Exists(path)) return false;

                string text = File.ReadAllText(path).Trim();
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture,
                                       DateTimeStyles.RoundtripKind, out DateTime written))
                    return false;

                age = DateTime.UtcNow - written.ToUniversalTime();
                if (age < TimeSpan.Zero) return true;   // clock moved backwards; treat as fresh
                return age < RequestMaxAge;
            }
            catch { return false; }
        }

        private static void WriteRequest(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Plain ISO-8601 UTC, one line. Deliberately not JSON: the only reader is the age
                // check above, and a text file is one thing that cannot fail to parse in a new way.
                File.WriteAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }
            catch
            {
                // A marker we could not write means at worst a second start attempt, which the
                // task's IgnoreNew policy swallows. Not worth failing the start over.
            }
        }
    }
}
