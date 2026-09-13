using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// Drivers and Windows Updates. Two columns: device and graphics drivers on the left, the state of
    /// Windows Update on the right. See Doku/PLAN_Drivers_And_Windows_Updates.md in the app repo.
    ///
    /// ── CENTER RENDERS. CENTER DETECTS NOTHING. ──────────────────────────────────────────────────
    /// Both halves come from the helper, which already owns them: MsiClawDriverCheckService plus the
    /// Intel DSA catalogue for the left column, WindowsUpdateCheckService for the right. Mutes, the
    /// beta opt-in and the installer cache are HELPER state - this screen may show them and change
    /// them through the pipe, never hold its own copy. A second opinion about the same question is
    /// the failure this project has already paid for at the TDP, the boost modes and the fan curve.
    ///
    /// ── The two halves cost very different amounts ───────────────────────────────────────────────
    /// The driver check is cheap because the helper caches it, so it runs when the screen opens. The
    /// Windows Update search is a network round trip to Microsoft measured at 29.1 s and 13.6 s, so it
    /// runs ONLY on the button, and the screen says when the answer was taken. A screen that says
    /// nothing for half a minute is indistinguishable from a hung one, hence the explicit busy line.
    ///
    /// ── What the right column deliberately does NOT show ─────────────────────────────────────────
    /// Drivers offered by Windows Update, and Defender's signature updates. The helper filters both
    /// out before they get here (Function.WindowsUpdateResult): Defender's entry is pending
    /// practically always, so a count including it can never reach zero, and Windows' own optional
    /// driver updates are the coarser copy of what the LEFT column already knows about this exact
    /// device. Decision of the user, 2026-09-13.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private DriversResult _driverResult;
        private WindowsUpdateResultDto _windowsUpdates;
        private bool _driversBusy;
        private bool _windowsUpdatesBusy;
        private string _driversError;
        private DateTime? _windowsUpdatesCheckedLocal;

        /// <summary>
        /// The rows on this screen a cursor can sit on, rebuilt on every render: the two interval
        /// settings and the shortcut into Windows' own page. The driver and update CARDS are
        /// deliberately not among them - there is nothing to do to a row that only reports.
        ///
        /// ⚠️ EACH ROW REMEMBERS WHICH COLUMN IT IS IN. The first version kept one flat list, so
        /// Down walked out of the left column and into the right one - and on a machine whose left
        /// column had nothing selectable (a non-Claw, where the helper answers "Claw only") the
        /// cursor could never reach the left side at all. That was the report. Left/Right crosses
        /// between columns now, Up/Down stays inside one.
        /// </summary>
        private sealed class DriverRow
        {
            public int Column;          // 0 = drivers, 1 = Windows Update
            public Action Activate;     // null for a row that only reports
            public FrameworkElement Element;
        }

        private readonly List<DriverRow> _driverRows = new List<DriverRow>();
        private int _driverRowIndex;

        // Deliberately generous. The driver check can go out to MSI and Intel on a cold cache, and the
        // Windows Update search is a network round trip that measured 29.1 s on this very machine - a
        // timeout below that would report "no answer" on a search that was still working.
        private static readonly TimeSpan DriverRequestTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan WindowsUpdateRequestTimeout = TimeSpan.FromSeconds(90);

        // ── Entry ──────────────────────────────────────────────────────────────────────────────
        private void OpenDrivers()
        {
            LeaveLibrary();
            _view = View.Drivers;
            _driverRowIndex = 0;
            RenderDrivers();
            RefreshTabStrip();   // the library's tabs are not navigation for this screen
            RefreshActionBar();

            // Cheap side: the helper serves its cached result unless something asks it not to.
            if (_driverResult == null && !_driversBusy) _ = RequestDriversAsync(force: false);
        }

        // ── Requests ───────────────────────────────────────────────────────────────────────────
        private async Task RequestDriversAsync(bool force)
        {
            _driversBusy = true;
            _driversError = null;
            RenderDriversIfStillOpen();
            try
            {
                if (!await EnsureHelperAsync())
                {
                    _driversError = "ClawTweaks is not running.";
                    return;
                }

                string json = await _helperPipe.RequestWithResultAsync(
                    new[]
                    {
                        new KeyValuePair<string, object>("CheckDriverUpdates", true),
                        new KeyValuePair<string, object>("ForceRefresh", force),
                    },
                    Shared.Enums.Function.DriverUpdateResult, DriverRequestTimeout);

                if (string.IsNullOrEmpty(json))
                {
                    // No answer is NOT "no drivers" - saying so would be the same mistake the right
                    // column avoids with resultCode.
                    _driversError = "No answer from ClawTweaks.";
                    return;
                }
                _driverResult = ParseJson<DriversResult>(json);
                if (_driverResult == null) _driversError = "The driver list could not be read.";
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("RequestDriversAsync failed: " + ex.Message);
                _driversError = "The driver check failed.";
            }
            finally
            {
                _driversBusy = false;
                RenderDriversIfStillOpen();
            }
        }

        private async Task RequestWindowsUpdatesAsync(bool force)
        {
            _windowsUpdatesBusy = true;
            RenderDriversIfStillOpen();
            try
            {
                if (!await EnsureHelperAsync())
                {
                    _windowsUpdates = new WindowsUpdateResultDto { ErrorMessage = "ClawTweaks is not running." };
                    return;
                }

                string json = await _helperPipe.RequestWithResultAsync(
                    new[]
                    {
                        new KeyValuePair<string, object>("CheckWindowsUpdates", true),
                        new KeyValuePair<string, object>("ForceRefresh", force),
                    },
                    Shared.Enums.Function.WindowsUpdateResult, WindowsUpdateRequestTimeout);

                _windowsUpdates = string.IsNullOrEmpty(json)
                    ? new WindowsUpdateResultDto { ErrorMessage = "No answer from ClawTweaks." }
                    : (ParseJson<WindowsUpdateResultDto>(json)
                       ?? new WindowsUpdateResultDto { ErrorMessage = "The answer could not be read." });

                _windowsUpdatesCheckedLocal = ParseUtc(_windowsUpdates.CheckedUtc);
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("RequestWindowsUpdatesAsync failed: " + ex.Message);
                _windowsUpdates = new WindowsUpdateResultDto { ErrorMessage = "The check failed." };
            }
            finally
            {
                _windowsUpdatesBusy = false;
                RenderDriversIfStillOpen();
            }
        }

        private async Task<bool> EnsureHelperAsync()
        {
            if (_helperPipe == null) return false;
            if (_helperPipe.IsConnected) return true;
            return await _helperPipe.ConnectAsync(TimeSpan.FromSeconds(6), m => Core.InstallLog.Write(m));
        }

        /// <summary>Redraws only while this screen is still the one on display - a request that comes
        /// back after the user has moved on must not repaint someone else's screen.</summary>
        private void RenderDriversIfStillOpen()
        {
            if (_view != View.Drivers) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RenderDriversIfStillOpen); return; }
            RenderDrivers();
            RefreshActionBar();
        }

        // ── Render ─────────────────────────────────────────────────────────────────────────────
        private void RenderDrivers()
        {
            BeginContent(centred: false);
            _driverRows.Clear();

            ContentHost.Children.Add(UiHelpers.Title("Drivers & Windows Updates"));

            var columns = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = BuildDriverColumn();
            Grid.SetColumn(left, 0);
            columns.Children.Add(left);

            var right = BuildWindowsUpdateColumn();
            Grid.SetColumn(right, 2);
            columns.Children.Add(right);

            ContentHost.Children.Add(columns);
        }

        private StackPanel BuildDriverColumn()
        {
            var stack = new StackPanel();
            stack.Children.Add(SectionHeading("Device drivers"));

            if (_driversBusy && _driverResult == null)
            {
                stack.Children.Add(UiHelpers.Body("Checking…"));
                return stack;
            }
            if (_driversError != null)
            {
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Drivers could not be checked", _driversError));
                return stack;
            }
            if (_driverResult == null)
            {
                stack.Children.Add(UiHelpers.Body("Not checked yet."));
                return stack;
            }
            if (!string.IsNullOrEmpty(_driverResult.Message))
            {
                // "Driver updates are only available on MSI Claw hardware" lands here. The setting
                // still belongs on screen: it is about future checks, not about this one.
                stack.Children.Add(UiHelpers.Body(_driverResult.Message));
                return stack;
            }

            var all = _driverResult.Drivers ?? new List<DriverEntryDto>();

            // Graphics on top and set apart, per the user's layout. The split already exists in the
            // model, so this reads it rather than guessing from the name.
            var graphics = all.Where(IsGraphics).ToList();
            var devices = all.Where(d => !IsGraphics(d)).ToList();

            if (graphics.Count > 0)
            {
                stack.Children.Add(SubHeading("Graphics"));
                foreach (var d in graphics) stack.Children.Add(BuildDriverCard(d, showHighlights: true));
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = UiHelpers.Card,
                    Margin = new Thickness(0, 6, 0, 12),
                });
            }

            stack.Children.Add(SubHeading("Devices"));
            if (devices.Count == 0)
                stack.Children.Add(UiHelpers.Body("Nothing to show for this device."));
            else
                foreach (var d in devices) stack.Children.Add(BuildDriverCard(d, showHighlights: false));

            if (_driverResult.LiveFetchSucceeded == false)
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Offline",
                    "The list could not be refreshed; showing what was known last."));

            return stack;
        }

        private static bool IsGraphics(DriverEntryDto d) =>
            string.Equals(d.Category, "Graphics", StringComparison.OrdinalIgnoreCase);

        private Border BuildDriverCard(DriverEntryDto d, bool showHighlights)
        {
            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = d.Name ?? "",
                FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });

            string installed = string.IsNullOrEmpty(d.InstalledVersion) ? Core.Loc.T("not installed") : d.InstalledVersion;
            stack.Children.Add(new TextBlock
            {
                Text = $"{Core.Loc.T("installed")} {installed}   ·   {Core.Loc.T("available")} {d.Version}",
                FontSize = 13, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });

            stack.Children.Add(BuildDriverChip(d));

            if (showHighlights && !string.IsNullOrWhiteSpace(d.Highlights))
                stack.Children.Add(new TextBlock
                {
                    Text = d.Highlights,
                    FontSize = 12, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });

            var card = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8),
                Child = stack,
            };

            // A cursor stop even though pressing it does nothing. That is the point: without it the
            // left column cannot be reached or scrolled with the pad at all, and reading the list is
            // exactly what somebody comes to this column for (user, 2026-09-13).
            RegisterDriverRow(0, card, null);
            return card;
        }

        /// <summary>
        /// The one-glance answer per row: a coloured pill instead of a line of prose that was only
        /// there when something was wrong. Before this, "up to date" was rendered as the ABSENCE of a
        /// line - which reads the same as a row that was never checked.
        ///
        /// Four states, and the quiet one is deliberate. Unknown is what a BIOS row from a foreign
        /// board comes back as (DriverMatchUtil.CompareMsiBiosCodes refuses to compare across board
        /// prefixes and returns null), and the helper does not offer those - so it stays grey and
        /// says "cannot tell" rather than colouring a row nobody should act on.
        /// </summary>
        private Border BuildDriverChip(DriverEntryDto d)
        {
            string text;
            Brush colour;

            if (d.Ignored)
            {
                // A muted row keeps its real state out of the chip on purpose: the user has said they
                // do not want to hear about it, and a green pill on a muted update would argue.
                text = Core.Loc.T("Muted");
                colour = UiHelpers.Subtle;
            }
            else
            {
                switch (d.UpdateStatus)
                {
                    case DriverUpdateStatusDto.UpdateAvailable:
                        text = d.IsBeta ? Core.Loc.T("Update available (beta)") : Core.Loc.T("Update available");
                        colour = UiHelpers.Warn;
                        break;
                    case DriverUpdateStatusDto.UpToDate:
                        text = Core.Loc.T("Up to date");
                        colour = UiHelpers.Ok;
                        break;
                    case DriverUpdateStatusDto.NotInstalled:
                        text = Core.Loc.T("Not installed");
                        colour = UiHelpers.Accent;
                        break;
                    default:
                        text = Core.Loc.T("Cannot tell");
                        colour = UiHelpers.Subtle;
                        break;
                }
            }

            // Tinted from the SAME brush as the text, so the pill cannot drift out of the theme the
            // way a hard-coded pair of colours would. 0.16 keeps it readable on the card behind it.
            var fill = colour.Clone();
            fill.Opacity = 0.16;

            return new Border
            {
                Background = fill,
                // A rectangle with soft corners, not a capsule: the pill shape read as a tag, and
                // these are states (user, 2026-09-13).
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 7, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = colour,
                },
            };
        }

        private StackPanel BuildWindowsUpdateColumn()
        {
            var stack = new StackPanel();
            stack.Children.Add(SectionHeading("Windows Update"));

            if (_windowsUpdatesBusy)
            {
                // The search really does take tens of seconds. Saying so is the difference between a
                // slow screen and one the user reads as frozen.
                stack.Children.Add(UiHelpers.Body("Checking with Windows… this can take up to a minute."));
                AppendWindowsUpdateFooterRows(stack);
                return stack;
            }

            if (_windowsUpdates == null)
            {
                stack.Children.Add(UiHelpers.Body("Not checked yet."));
                stack.Children.Add(UiHelpers.Body("The check asks Microsoft directly and takes a moment."));
                AppendWindowsUpdateFooterRows(stack);
                return stack;
            }

            if (!string.IsNullOrEmpty(_windowsUpdates.ErrorMessage))
            {
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Could not check", _windowsUpdates.ErrorMessage));
                AppendWindowsUpdateFooterRows(stack);
                return stack;
            }

            // resultCode 2 is the only "the search really ran" answer. Anything else must not be
            // rendered as "up to date" - that is the whole reason it travels over the pipe.
            if (_windowsUpdates.ResultCode != 2)
            {
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "No result from Windows Update",
                    Core.Loc.F("The search ended with code {0}.", _windowsUpdates.ResultCode)));
                AppendWindowsUpdateFooterRows(stack);
                return stack;
            }

            var updates = _windowsUpdates.Updates ?? new List<WindowsUpdateEntryDto>();
            if (updates.Count == 0)
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Ok, "Up to date",
                    "Windows has nothing pending for this machine."));
            else
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning,
                    Core.Loc.F("{0} update(s) ready", updates.Count),
                    "Install them in Windows Update."));

            if (_windowsUpdatesCheckedLocal.HasValue)
                stack.Children.Add(new TextBlock
                {
                    Text = Core.Loc.F("Checked at {0}", _windowsUpdatesCheckedLocal.Value.ToString("t")),
                    FontSize = 12, Foreground = UiHelpers.Subtle, Margin = new Thickness(2, 2, 0, 10),
                });

            foreach (var u in updates) stack.Children.Add(BuildWindowsUpdateCard(u));

            if (_windowsUpdates.RebootRequired)
                stack.Children.Add(UiHelpers.StatusRow(StatusKind.Warning, "Restart pending",
                    "Windows needs a restart to finish an update."));

            AppendWindowsUpdateFooterRows(stack);
            return stack;
        }

        private Border BuildWindowsUpdateCard(WindowsUpdateEntryDto u)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = u.Title ?? "",
                FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });

            var facts = new List<string>();
            if (!string.IsNullOrEmpty(u.Kb)) facts.Add("KB" + u.Kb);
            if (!string.IsNullOrEmpty(u.Severity)) facts.Add(u.Severity);
            if (u.SizeBytes > 0) facts.Add($"{u.SizeBytes / 1024d / 1024d:N0} MB");
            // 1 = always, 2 = can require. 0 says nothing worth a line.
            if (u.RebootBehavior == 1) facts.Add(Core.Loc.T("restart required"));
            else if (u.RebootBehavior == 2) facts.Add(Core.Loc.T("restart possible"));

            if (facts.Count > 0)
                stack.Children.Add(new TextBlock
                {
                    Text = string.Join("   ·   ", facts),
                    FontSize = 12, Foreground = UiHelpers.Subtle, Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

            var card = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8),
                Child = stack,
            };
            RegisterDriverRow(1, card, null);
            return card;
        }

        /// <summary>The one thing this column does besides report: a real BUTTON into Windows' own
        /// page. It was briefly a settings-style row and that was wrong - it is an action, not a
        /// value (user, 2026-09-13). The interval that sat under it moved to Center settings.</summary>
        private void AppendWindowsUpdateFooterRows(StackPanel stack)
        {
            var button = new Button
            {
                Content = Core.Loc.T("Open Windows Update"),
                Style = (Style)Application.Current.Resources["SetupButton"],
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 16, 0, 0),
            };
            button.Click += (_, __) => OpenWindowsUpdateSettings();

            RegisterDriverRow(1, button, OpenWindowsUpdateSettings);
            stack.Children.Add(button);
        }

        /// <summary>
        /// Makes an element a cursor stop. <paramref name="activate"/> may be null: a row that only
        /// reports still has to be somewhere the cursor can go, or a column of pure information
        /// cannot be reached or scrolled with a pad.
        ///
        /// The selection is drawn as an outline on whatever the element already is, so a card still
        /// looks like a card and a button still looks like a button.
        /// </summary>
        private void RegisterDriverRow(int column, FrameworkElement element, Action activate)
        {
            int index = _driverRows.Count;
            bool selected = index == _driverRowIndex;

            if (element is Control control)
            {
                control.BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent;
                control.BorderThickness = new Thickness(selected ? 2 : 0);
            }
            else if (element is Border border)
            {
                border.BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent;
                border.BorderThickness = new Thickness(selected ? 2 : 0);
            }

            if (activate != null)
            {
                element.Cursor = Cursors.Hand;
                element.MouseLeftButtonUp += (_, __) => { _driverRowIndex = index; activate(); };
            }

            _driverRows.Add(new DriverRow { Column = column, Activate = activate, Element = element });
        }

        /// <summary>1 \u2192 2 \u2192 3 \u2192 4 \u2192 off \u2192 1. Off is reachable on purpose: a background check that
        /// reaches the network and cannot be switched off is a behaviour, not a setting.</summary>
        private static int NextInterval(int weeks) =>
            weeks >= Core.CenterSettings.IntervalMaxWeeks ? Core.CenterSettings.IntervalOff
            : weeks <= Core.CenterSettings.IntervalOff ? Core.CenterSettings.IntervalMinWeeks
            : weeks + 1;

        private static string IntervalLabel(int weeks)
        {
            if (weeks <= Core.CenterSettings.IntervalOff) return Core.Loc.T("Never");
            if (weeks == 1) return Core.Loc.T("Every week");
            return Core.Loc.F("Every {0} weeks", weeks);
        }

        // \u2500\u2500 Navigation \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        private void MoveDriversSelection(PadButton dir)
        {
            if (_driverRows.Count == 0) return;
            if (_driverRowIndex < 0 || _driverRowIndex >= _driverRows.Count) _driverRowIndex = 0;

            int current = _driverRowIndex;
            int column = _driverRows[current].Column;
            int next = current;

            if (dir == PadButton.Up || dir == PadButton.Down)
            {
                int step = dir == PadButton.Up ? -1 : 1;
                for (int i = current + step; i >= 0 && i < _driverRows.Count; i += step)
                    if (_driverRows[i].Column == column) { next = i; break; }
            }
            else if (dir == PadButton.Left || dir == PadButton.Right)
            {
                // Cross to the other column and land on ITS first row - not on the nearest index,
                // which would depend on how many driver rows happen to be listed above.
                int target = dir == PadButton.Left ? 0 : 1;
                for (int i = 0; i < _driverRows.Count; i++)
                    if (_driverRows[i].Column == target) { next = i; break; }
            }
            else return;

            if (next == current) return;
            _driverRowIndex = next;
            RenderDrivers();

            // Without this the cursor walks off the bottom of the viewport and the screen looks
            // frozen - the rows below the fold are exactly the ones this navigation exists for.
            _driverRows[_driverRowIndex].Element?.BringIntoView();
        }

        private static TextBlock SectionHeading(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 17, FontWeight = FontWeights.Bold, Foreground = UiHelpers.Text,
            Margin = new Thickness(0, 0, 0, 10),
        };

        private static TextBlock SubHeading(string text) => new TextBlock
        {
            Text = Core.Loc.T(text),
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Subtle,
            Margin = new Thickness(2, 0, 0, 6),
        };

        // ── Action bar ─────────────────────────────────────────────────────────────────────────
        private void RefreshDriversActionBar()
        {
            // \u24B6 acts on the ROW the cursor is on - the two interval settings and the shortcut into
            // Windows. The two check buttons keep their own chips: they are what someone came here to
            // press, and burying them one cursor move deep would be the wrong trade.
            // Enabled only when the row under the cursor actually does something - most of them are
            // driver and update cards, which exist to be read.
            bool canAct = _driverRowIndex >= 0 && _driverRowIndex < _driverRows.Count
                          && _driverRows[_driverRowIndex].Activate != null;
            AddAction(PadButton.A, "Open", canAct, () =>
            {
                if (canAct) _driverRows[_driverRowIndex].Activate();
            });
            AddAction(PadButton.X, "Check Windows Update", !_windowsUpdatesBusy, () => _ = RequestWindowsUpdatesAsync(force: true));
            AddAction(PadButton.Y, "Refresh drivers", !_driversBusy, () => _ = RequestDriversAsync(force: true));
            AddAction(PadButton.B, "Back", true, GoHome);
            AddScrollHint();
        }

        /// <summary>
        /// ⚠️ An UNKNOWN ms-settings id opens the Settings HOME PAGE - no error, no return value, and
        /// ShellExec reports success either way. That is exactly how the FSE button broke on
        /// 2026-09-10. This id is read out of C:\Windows\ImmersiveControlPanel, not guessed, and so
        /// must any that is added next to it.
        /// </summary>
        private void OpenWindowsUpdateSettings()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:windowsupdate-action",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write("Opening Windows Update failed: " + ex.Message);
            }
        }

        // ── Parsing ────────────────────────────────────────────────────────────────────────────
        private static T ParseJson<T>(string json) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Core.InstallLog.Write($"Drivers area: could not parse {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static DateTime? ParseUtc(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime()
                : (DateTime?)null;
        }

        // ── Wire shapes ────────────────────────────────────────────────────────────────────────
        // Mirrors of what the helper sends, kept deliberately SMALL: only the fields this screen
        // draws. The helper's payload carries more (download URLs, cached installers, match scores)
        // and every one of those belongs to an action this screen does not have.

        private enum DriverUpdateStatusDto { Unknown = 0, UpToDate = 1, UpdateAvailable = 2, NotInstalled = 3 }

        private sealed class DriversResult
        {
            public bool LiveFetchSucceeded { get; set; }
            public string Message { get; set; }
            public List<DriverEntryDto> Drivers { get; set; }
        }

        private sealed class DriverEntryDto
        {
            public string Name { get; set; }
            public string Category { get; set; }
            public string Version { get; set; }
            public string InstalledVersion { get; set; }
            public DriverUpdateStatusDto UpdateStatus { get; set; }
            public string Highlights { get; set; }
            public bool IsBeta { get; set; }
            public bool Ignored { get; set; }
            public string ProviderScope { get; set; }
        }

        private sealed class WindowsUpdateResultDto
        {
            public int ResultCode { get; set; }
            public string CheckedUtc { get; set; }
            public bool RebootRequired { get; set; }
            public List<WindowsUpdateEntryDto> Updates { get; set; }
            public string ErrorMessage { get; set; }
        }

        private sealed class WindowsUpdateEntryDto
        {
            public string Title { get; set; }
            public string Kb { get; set; }
            public string Severity { get; set; }
            public long SizeBytes { get; set; }
            public int RebootBehavior { get; set; }
        }
    }
}
