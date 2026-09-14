using System;
using System.IO;

namespace ClawTweaksCenter.Ui
{
    /// <summary>
    /// Evidence for ONE question: what makes the library go black and stand still for about a second
    /// and a half, right after the virtual controller is mounted?
    ///
    /// It started as a controller-poll trace and outgrew the name - the poll was a real defect but not
    /// the cause, and the same file now carries the window-mode decision that is. One file, one clock,
    /// so the two can be read against each other and against the helper's log.
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
    internal static class UiStallTrace
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
                string path = System.IO.Path.Combine(dir, "center_ui_stall.log");

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

        // ── WHAT THE UI THREAD WAS DOING ────────────────────────────────────────────────────────
        //
        // Five rounds of measurement established THAT the UI thread stops for about 1.2 s and never
        // WHAT it is stopped on, and three wrong conclusions were drawn from the company it keeps:
        // the controller poll (a real defect, not this one), the garbage collector (ruled out by the
        // pause counters), the fullscreen reflow (ruled out by its own guard, which now holds and
        // logs that it held). Each of those was something that happened NEARBY.
        //
        // Two witnesses close that gap, and between them they cover both ways a WPF UI thread can go
        // dark:
        //
        //   the dispatcher operation  - our own code, queued and running. Dispatcher.Hooks reports
        //                               it, and if one is in flight when the stall is measured, the
        //                               work is OURS and has a name.
        //   the window message        - a WndProc that has not returned. If NO dispatcher operation
        //                               is in flight but a message arrived just before the stall
        //                               began, the thread is inside Windows' own handling of it -
        //                               WM_DISPLAYCHANGE and WM_DWMCOMPOSITIONCHANGED both make WPF
        //                               rebuild its rendering surface, which is what a black window
        //                               with one painted rectangle looks like.
        //
        // Both are written down on the UI thread as they happen and only READ when a stall is
        // reported, so they cost a field assignment per operation and nothing at all otherwise.

        private static int _attached;
        private static volatile string _currentOp;
        private static long _currentOpSince;
        private static volatile string _lastMessage;
        private static long _lastMessageAt;

        /// <summary>
        /// Starts the two witnesses for this window. Call once, from the UI thread, for the window
        /// the library lives in. Safe to call more than once.
        /// </summary>
        internal static void Attach(System.Windows.Window window)
        {
            if (window == null) return;
            if (System.Threading.Interlocked.Exchange(ref _attached, 1) != 0) return;

            try
            {
                System.Windows.Threading.Dispatcher d = window.Dispatcher;
                d.Hooks.OperationStarted += (_, e) =>
                {
                    _currentOp = DescribeOperation(e.Operation);
                    System.Threading.Volatile.Write(ref _currentOpSince, System.Diagnostics.Stopwatch.GetTimestamp());
                };
                d.Hooks.OperationCompleted += (_, __) => _currentOp = null;
                d.Hooks.OperationAborted += (_, __) => _currentOp = null;
            }
            catch (Exception ex) { Write("could not hook the dispatcher: " + ex.Message); }

            try
            {
                if (System.Windows.PresentationSource.FromVisual(window) is System.Windows.Interop.HwndSource src)
                    src.AddHook(MessageHook);
                else
                    window.SourceInitialized += (_, __) =>
                    {
                        try
                        {
                            if (System.Windows.PresentationSource.FromVisual(window) is System.Windows.Interop.HwndSource late)
                                late.AddHook(MessageHook);
                        }
                        catch { }
                    };
            }
            catch (Exception ex) { Write("could not hook the window messages: " + ex.Message); }

            try
            {
                // Center's OWN clock for the display change. The previous round compared a stall here
                // against a timestamp from the helper's log and concluded the change came AFTER the
                // stall - which decided the direction of the whole investigation on two processes'
                // clocks and two different notification paths. One file, one clock, no inference.
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, __) =>
                    Write("display settings changed (Center's own clock)");
            }
            catch { }

            Write("stall witnesses attached (dispatcher operation + window messages)");
        }

        /// <summary>Records the message and returns without handling it. Never sets handled.</summary>
        private static IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                // Only the ones that can plausibly cost a second. Logging every WM_MOUSEMOVE would
                // bury the answer in noise and slow the very thread being measured.
                case 0x007E: Note("WM_DISPLAYCHANGE"); break;
                case 0x0219: Note("WM_DEVICECHANGE"); break;
                case 0x001A: Note("WM_SETTINGCHANGE"); break;
                case 0x02E0: Note("WM_DPICHANGED"); break;
                case 0x031E: Note("WM_DWMCOMPOSITIONCHANGED"); break;
                case 0x031F: Note("WM_DWMNCRENDERINGCHANGED"); break;
                case 0x0320: Note("WM_DWMCOLORIZATIONCOLORCHANGED"); break;
                case 0x0018: Note("WM_SHOWWINDOW"); break;
                case 0x0047: Note("WM_WINDOWPOSCHANGED"); break;
                case 0x0006: Note("WM_ACTIVATE"); break;
                case 0x0011: Note("WM_QUERYENDSESSION"); break;
                case 0x02B1: Note("WM_WTSSESSION_CHANGE"); break;
                case 0x0218: Note("WM_POWERBROADCAST"); break;
            }
            return IntPtr.Zero;
        }

        private static void Note(string name)
        {
            _lastMessage = name;
            System.Threading.Volatile.Write(ref _lastMessageAt, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// A name for the operation. DispatcherOperation does not expose the delegate it will run, so
        /// this reaches for the private field and falls back to the priority alone. Reflection into a
        /// framework internal is not something to ship in a feature; in a diagnostic that exists to
        /// end a five-round investigation it is worth one guarded try block.
        /// </summary>
        private static string DescribeOperation(System.Windows.Threading.DispatcherOperation op)
        {
            if (op == null) return null;
            string priority;
            try { priority = op.Priority.ToString(); } catch { priority = "?"; }

            try
            {
                var field = typeof(System.Windows.Threading.DispatcherOperation).GetField(
                    "_method", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field?.GetValue(op) is Delegate method)
                {
                    string name = method.Method?.Name ?? "?";
                    string owner = method.Method?.DeclaringType?.Name ?? "?";

                    // Our own probe, called by its name. It is queued by the very measurement that
                    // reads this, so seeing it here means nothing was found - and a line that says so
                    // is worth more than one that looks like a culprit.
                    if (name.Contains("MeasureUiResponsiveness"))
                        return "(this trace's own probe - nothing else was running)";

                    return $"{owner}.{name} at {priority}";
                }
            }
            catch { }
            return "unnamed at " + priority;
        }

        /// <summary>Both witnesses, as one clause for a stall line. Read from any thread.</summary>
        internal static string Witnesses()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

            string op = _currentOp;
            string opPart = op == null
                ? "no dispatcher operation in flight"
                : $"dispatcher op '{op}' running for " +
                  $"{(long)((now - System.Threading.Volatile.Read(ref _currentOpSince)) / ticksPerMs)}ms";

            string msg = _lastMessage;
            string msgPart = msg == null
                ? "no notable window message yet"
                : $"last window message {msg}, " +
                  $"{(long)((now - System.Threading.Volatile.Read(ref _lastMessageAt)) / ticksPerMs)}ms ago";

            return opPart + "; " + msgPart;
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
