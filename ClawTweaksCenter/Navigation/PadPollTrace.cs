using System;
using System.IO;

namespace ClawTweaksCenter.Navigation
{
    /// <summary>
    /// Evidence for ONE question: is the controller poll what makes the library stutter and go black
    /// while the virtual pad is being mounted?
    ///
    /// WHY IT EXISTS. Reported 2026-09-14: entering the library, the moment the helper starts mounting
    /// the virtual controller, large parts of the shelf go black and the window stands still for a few
    /// seconds, then recovers. Before the mount the same shelf can be scrolled end to end with the
    /// hardware pad. The suspected mechanism is XInputGetState on EMPTY user slots - Microsoft's own
    /// guidance is not to ask an empty slot every frame - and the mount is exactly the window in which
    /// every slot is empty: the pad has left XInput mode and the virtual one has not arrived yet.
    ///
    /// This file does not fix anything. It writes down three numbers so the answer is measured rather
    /// than argued:
    ///
    ///   poll   how long the XInput calls themselves took, and how many slots answered
    ///   gap    how long the poll loop actually slept between two rounds (asked for: 40 ms)
    ///   ui     how long a do-nothing item queued at input priority waited for the UI thread
    ///
    /// The third is the one that separates the two candidates. If "poll" is large the controller poll
    /// is the cost. If "poll" is small but "ui" is large, the UI thread is being held up by something
    /// else in the same window - the PnP broadcasts that HidHide, VIIPER and the phantom cleanup set
    /// off - and this class has ruled the poll out.
    ///
    /// NOTHING IS WRITTEN unless a threshold breaks, so a healthy session produces a file with two
    /// lines in it. Timestamps are local wall-clock so they line up with the helper's own log.
    /// </summary>
    internal static class PadPollTrace
    {
        /// <summary>Longer than this in the XInput calls of one round is worth a line (ms).</summary>
        internal const int PollWarnMs = 12;

        /// <summary>A loop round that took this much longer than its 40 ms cadence (ms).</summary>
        internal const int GapWarnMs = 120;

        /// <summary>The UI thread did not get to a queued no-op within this (ms).</summary>
        internal const int UiWarnMs = 150;

        /// <summary>Keeps the file from growing without bound across months of sessions.</summary>
        private const long MaxBytes = 1024 * 1024;

        private static readonly object Lock = new object();
        private static readonly string Path_ = BuildPath();

        private static string BuildPath()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawTweaks");
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "center_pad_poll.log");

                // Start over rather than trim: a stall log is only ever read for the newest session,
                // and a half-file is harder to explain than a fresh one.
                try { if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path); }
                catch { }

                return path;
            }
            catch { return null; }
        }

        /// <summary>
        /// What the garbage collector had done at one instant. Taken before a measurement and read
        /// back after it, the difference says whether the pause we measured WAS a GC pause.
        ///
        /// This is the discriminator the first round of measurements asked for. On 2026-09-14 the UI
        /// thread needed 1204 ms for a no-op while, in the same 200 ms, a single XInputGetState on a
        /// CONNECTED slot appeared to take 830 ms. A slow driver call cannot explain both - those are
        /// two different threads - but a stop-the-world collection explains them together: the UI
        /// thread is suspended outright, and the poll thread, which is in unmanaged code and cannot be
        /// suspended there, is held at the boundary until the collection finishes and so bills the
        /// wait to the call it was making.
        ///
        /// GetTotalPauseDuration counts only the time execution was actually stopped, so a background
        /// collection running alongside us does not inflate it.
        /// </summary>
        internal struct GcMark
        {
            public int Gen0, Gen1, Gen2;
            public TimeSpan Pause;
        }

        internal static GcMark MarkGc()
        {
            return new GcMark
            {
                Gen0 = GC.CollectionCount(0),
                Gen1 = GC.CollectionCount(1),
                Gen2 = GC.CollectionCount(2),
                Pause = GC.GetTotalPauseDuration(),
            };
        }

        /// <summary>Reads <see cref="MarkGc"/> again and describes the difference in one clause.</summary>
        internal static string SinceGc(GcMark before)
        {
            GcMark now = MarkGc();
            long paused = (long)(now.Pause - before.Pause).TotalMilliseconds;
            int g0 = now.Gen0 - before.Gen0, g1 = now.Gen1 - before.Gen1, g2 = now.Gen2 - before.Gen2;
            if (paused <= 0 && g0 == 0 && g1 == 0 && g2 == 0) return "no GC";
            return $"GC stopped the world {paused}ms (gen0 +{g0}, gen1 +{g1}, gen2 +{g2})";
        }

        internal static void Write(string message)
        {
            if (Path_ == null) return;
            try
            {
                lock (Lock)
                    File.AppendAllText(Path_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch { /* a diagnostic must never be able to break navigation */ }
        }
    }
}
