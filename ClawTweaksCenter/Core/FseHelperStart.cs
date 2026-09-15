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
        ///   2. a helper is already ALIVE → nothing to do. Alive means a fresh heartbeat and a live
        ///      pid, NOT merely a process of that name: measured 2026-09-14 those differ by 15s at
        ///      boot, and the name test skipped every start this method exists to make. See
        ///      HelperControl.HelperAlive.
        ///   3. a recent request exists  → someone already asked; asking again is how you get two
        ///   4. the task is PROVABLY absent → the helper has not set itself up yet. Center must NOT
        ///                                 create it: that path is the helper's, deliberately, and
        ///                                 it owns the single UAC prompt that comes with it. A query
        ///                                 that could not answer is not an absent task and does not
        ///                                 stop us - see HelperControl.TaskPresence.
        /// </summary>
        internal static string TryStartHelper()
        {
            // ⚠️ THE WHOLE MECHANISM IS OFF SINCE 2026-09-15, and this return is the last of three
            // places that make sure of it: the call site in App.xaml.cs is commented out, the
            // setting CenterSettings.FseStartsHelper is commented out, and nothing below this line
            // runs even if somebody wires the caller back up without reading either.
            //
            // WHY: measured across four boots on 2026-09-14, the helper's logon trigger fires at
            // about +16.7s and Center runs at about +21.7s, so the scheduler refused every request
            // as a duplicate (event 322). It was never once the earlier of the two. The startup
            // problem this was aimed at got solved in the scheduled task itself — see
            // Doku/TODO_Scheduled_Task_Fast_Controller.md in the helper repo.
            //
            // The rest of this file is kept because the GUARDS are the hard part and the reasoning
            // in them is still correct. If a machine ever does serve the logon trigger late, this is
            // the shape the answer takes; re-enabling means all three places, not just this one.
            return "disabled: the FSE helper start was removed on 2026-09-15 (measured: no effect)";

            // if (!CenterSettings.FseStartsHelper)
            //     return "disabled: experimental setting is off";
#pragma warning disable CS0162 // unreachable code - deliberate, see above

            if (!IsFseStartApp(out string fseDetail))
                return $"skipped: not the FSE start app ({fseDetail})";

            if (HelperControl.HelperAlive(out string aliveDetail))
                return $"skipped: helper already alive ({aliveDetail})";

            string folder = Shared.IPC.HelperHandover.ResolveFolder(PackageFamily);
            string requestPath = Path.Combine(folder, RequestFileName);

            if (TryReadRecentRequest(requestPath, out TimeSpan age))
                return $"skipped: a start was already requested {age.TotalSeconds:F0}s ago";

            // Only a DEFINITE "not there" stops us. An inconclusive query must not, and that is not
            // caution for its own sake: measured 2026-09-14 this guard vetoed a start while the task
            // was running, purely because schtasks was slow under boot load. Acting on Unknown costs
            // at worst one refused request; believing Unknown costs the whole mechanism.
            HelperControl.TaskPresence presence = HelperControl.QueryScheduledTask(out string taskDetail);
            if (presence == HelperControl.TaskPresence.Absent)
                return $"skipped: no scheduled task yet, the helper registers it itself ({taskDetail})";

            WriteRequest(requestPath);

            bool ok = HelperControl.RunScheduledTask(out string runDetail);
            string qualifier = presence == HelperControl.TaskPresence.Unknown
                ? $" [task presence unknown: {taskDetail}]"
                : "";
            return ok
                ? $"requested: scheduled task run ({runDetail}){qualifier}"
                : $"FAILED: {runDetail}{qualifier}";
#pragma warning restore CS0162
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
