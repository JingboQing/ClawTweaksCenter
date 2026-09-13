using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The notification list, and the counter beside the clock that leads to it.
    ///
    /// ── WHY IT IS IN THE FOOTER AND NOT A BANNER ────────────────────────────────────────────────
    /// The messages that collect here - a driver update, something pending in Windows Update, a new
    /// widget build - are all "worth knowing, none of them now". A banner on the start screen would
    /// interrupt the one thing the user opened Center to do; a small count next to the clock is
    /// there whenever they happen to look, on both screens they can start on.
    ///
    /// ── LT, and only where LT is free ───────────────────────────────────────────────────────────
    /// The letter bar owns LT in All and Not installed, the ROM tab cycles systems with it. So the
    /// binding is decided in RefreshActionBar, AFTER every screen has said what it wants, and the
    /// keycap appears only when the binding actually exists. That is the letter bar's own rule and
    /// the reason nobody has to press a key to find out whether it does anything.
    ///
    /// ⚠️ The COUNT stays visible either way. The keycap is an affordance and hiding it where the key
    /// is taken is right; hiding the fact that three things are waiting, because this happens to be
    /// the ROM tab, would be hiding information for a reason the user cannot see.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private int _notificationsSelectedIndex;
        private List<Core.Notification> _notificationsShown = new List<Core.Notification>();

        /// <summary>Where B goes back to. The list is reachable from Home and from the library, and
        /// dropping someone onto the start screen after they opened it mid-library would be a worse
        /// answer than remembering.</summary>
        private View _notificationsCameFrom = View.Home;

        /// <summary>Set while the list is on screen so the store's Changed event can redraw it.
        /// Subscribed once, in the constructor path, because a per-open subscription that is missed
        /// on one exit path leaks a handler for the lifetime of the window.</summary>
        private void HookNotifications()
        {
            Core.Notifications.Changed += () =>
            {
                // Raised from a background check, so it arrives on a worker thread.
                if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)OnNotificationsChanged); return; }
                OnNotificationsChanged();
            };
        }

        private void OnNotificationsChanged()
        {
            RefreshActionBar();                     // redraws the counter through its post-step
            if (_view == View.Notifications) RenderNotifications();
        }

        // ── The counter beside the clock ────────────────────────────────────────────────────────
        private void RefreshNotificationIndicator(bool bindLeftTrigger)
        {
            if (FooterNotify == null) return;

            int unread = Core.Notifications.UnreadCount();
            if (unread <= 0 || _view == View.Notifications)
            {
                // Nothing waiting, or the list is already open: an unread count pointing at the
                // screen you are looking at is noise.
                FooterNotify.Visibility = Visibility.Collapsed;
                FooterNotify.Child = null;
                return;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand };
            row.Children.Add(new TextBlock
            {
                Text = "",                    // Segoe MDL2 "Ringer"
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = UiHelpers.Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = unread.ToString(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Accent,
                VerticalAlignment = VerticalAlignment.Center,
            });

            if (bindLeftTrigger)
            {
                _liveActions[PadButton.LT] = OpenNotifications;
                var cap = BuildKeyCap("LT");
                if (cap is FrameworkElement fe) fe.Margin = new Thickness(7, 0, 0, 0);
                row.Children.Add(cap);
            }

            FooterNotify.Child = row;
            FooterNotify.Visibility = Visibility.Visible;
            FooterNotify.MouseLeftButtonUp -= NotifyClicked;
            FooterNotify.MouseLeftButtonUp += NotifyClicked;
        }

        private void NotifyClicked(object sender, MouseButtonEventArgs e) => OpenNotifications();

        // ── The list ────────────────────────────────────────────────────────────────────────────
        private void OpenNotifications()
        {
            _notificationsCameFrom = _view == View.Notifications ? _notificationsCameFrom : _view;
            if (_view == View.Library) LeaveLibrary();
            _view = View.Notifications;
            _notificationsSelectedIndex = 0;
            RenderNotifications();
            RefreshActionBar();
        }

        private void CloseNotifications()
        {
            if (_notificationsCameFrom == View.Library) { OpenLibrary(); return; }
            GoHome();
        }

        private void RenderNotifications()
        {
            BeginContent(centred: false);
            _notificationsShown = Core.Notifications.All();

            ContentHost.Children.Add(UiHelpers.Title("Notifications"));

            if (_notificationsShown.Count == 0)
            {
                ContentHost.Children.Add(UiHelpers.Body("Nothing here yet."));
                ContentHost.Children.Add(UiHelpers.Body(
                    "Center tells you here when a driver, a Windows update or a new widget build turns up."));
                return;
            }

            for (int i = 0; i < _notificationsShown.Count; i++)
                ContentHost.Children.Add(BuildNotificationCard(_notificationsShown[i], i));
        }

        private Border BuildNotificationCard(Core.Notification n, int index)
        {
            bool selected = index == _notificationsSelectedIndex;

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = GlyphFor(n.Kind),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = n.IsUnread ? UiHelpers.Accent : UiHelpers.Subtle,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
            head.Children.Add(new TextBlock
            {
                Text = n.Title ?? "",
                FontSize = 15,
                // Unread is carried by WEIGHT and by the dot below, not by colour alone - the cards
                // sit on the same background and a colour difference at this size is easy to miss.
                FontWeight = n.IsUnread ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (n.IsUnread)
                head.Children.Add(new Border
                {
                    Width = 7, Height = 7,
                    CornerRadius = new CornerRadius(999),
                    Background = UiHelpers.Accent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                });

            var stack = new StackPanel();
            stack.Children.Add(head);

            if (!string.IsNullOrWhiteSpace(n.Detail))
                stack.Children.Add(new TextBlock
                {
                    Text = n.Detail,
                    FontSize = 13, Foreground = UiHelpers.Subtle, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, 4, 0, 0),
                });

            stack.Children.Add(new TextBlock
            {
                Text = FormatWhen(n.CreatedUtc),
                FontSize = 12, Foreground = UiHelpers.Subtle, Opacity = 0.8,
                Margin = new Thickness(24, 5, 0, 0),
            });

            var pad = new Thickness(16, 12, 16, 12);
            var card = new Border
            {
                Background = UiHelpers.Card,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = selected ? UiHelpers.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(selected ? 2 : 0),
                Padding = selected ? Deflate(pad, 2) : pad,
                Cursor = Cursors.Hand,
                Child = stack,
            };
            card.MouseLeftButtonUp += (_, __) =>
            {
                _notificationsSelectedIndex = index;
                ActivateNotification(n);
            };
            return card;
        }

        /// <summary>Opening one takes you where it is about, and marks it read on the way. Marking it
        /// read WITHOUT going anywhere would make the card a thing you dismiss rather than act on.</summary>
        private void ActivateNotification(Core.Notification n)
        {
            Core.Notifications.MarkRead(n.Key);
            switch (n.Kind)
            {
                case "driver":
                case "windows":
                    OpenDrivers();
                    break;
                case "widget":
                    OpenBrowse();
                    break;
                default:
                    RenderNotifications();
                    RefreshActionBar();
                    break;
            }
        }

        private static string GlyphFor(string kind)
        {
            switch (kind)
            {
                case "driver": return "";     // PC
                case "windows": return "";    // Sync
                case "widget": return "";     // Download
                default: return "";           // Info
            }
        }

        /// <summary>Today shows a time, everything else a date. "3 days ago" was the other option and
        /// it answers a question nobody asked here - the list is short and already newest-first.</summary>
        private static string FormatWhen(DateTime createdUtc)
        {
            var local = createdUtc.ToLocalTime();
            return local.Date == DateTime.Now.Date
                ? local.ToString("t")
                : local.ToString("d MMM, HH:mm");
        }

        // ── Navigation ──────────────────────────────────────────────────────────────────────────
        private void MoveNotificationsSelection(PadButton dir)
        {
            if (_notificationsShown.Count == 0) return;
            int next = _notificationsSelectedIndex;
            if (dir == PadButton.Up) next--;
            else if (dir == PadButton.Down) next++;
            else return;

            if (next < 0) next = 0;
            if (next > _notificationsShown.Count - 1) next = _notificationsShown.Count - 1;
            if (next == _notificationsSelectedIndex) return;

            _notificationsSelectedIndex = next;
            RenderNotifications();
        }

        private void RefreshNotificationsActionBar()
        {
            bool any = _notificationsShown.Count > 0;
            bool anyUnread = Core.Notifications.UnreadCount() > 0;

            AddAction(PadButton.A, "Open", any, () =>
            {
                if (_notificationsSelectedIndex >= 0 && _notificationsSelectedIndex < _notificationsShown.Count)
                    ActivateNotification(_notificationsShown[_notificationsSelectedIndex]);
            });
            AddAction(PadButton.Y, "Mark all as read", anyUnread, Core.Notifications.MarkAllRead);
            AddAction(PadButton.B, "Back", true, CloseNotifications);
            AddScrollHint();
        }
    }
}
