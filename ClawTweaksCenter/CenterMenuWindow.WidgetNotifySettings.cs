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
    /// How often a new widget build turns into a notification, and whether test builds count.
    ///
    /// ── WHY A SCREEN AND NOT TWO ROWS IN THE LIST ───────────────────────────────────────────────
    /// Update &amp; Release navigates a two-column GRID of build cards with all four directions, and
    /// the two settings are not build cards. Hanging them off the end of that grid would put a
    /// cursor that means "which version do I install" onto a row that means "how often do you tell
    /// me" - the same press doing two different kinds of thing. Ⓧ was the one free button on that
    /// screen, and one press is a cheaper price than an ambiguous cursor.
    ///
    /// ⚠️ THE INTERVAL HERE THROTTLES THE MESSAGE, NOT THE SEARCH. Center fetches the release list on
    /// every start either way (FetchGitHubAsync) - so the screen says "tell me about", not "look
    /// for". A setting that claims to control a fetch it does not control is a setting that will be
    /// blamed for the next surprise.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private int _widgetNotifyRowIndex;

        private void OpenWidgetNotifySettings()
        {
            _view = View.WidgetNotifySettings;
            _widgetNotifyRowIndex = 0;
            RenderWidgetNotifySettings();
            RefreshActionBar();
        }

        private void RenderWidgetNotifySettings()
        {
            BeginContent(centred: false);
            ContentHost.Children.Add(UiHelpers.Title("Update notifications"));
            ContentHost.Children.Add(UiHelpers.Body(
                "Center already looks for new versions at every start. This decides how often it says so."));

            ContentHost.Children.Add(BuildWidgetNotifyRow(0,
                "Tell me about new versions",
                IntervalLabel(Core.CenterSettings.WidgetUpdateNotifyIntervalWeeks)));

            ContentHost.Children.Add(BuildWidgetNotifyRow(1,
                "Include test versions",
                Core.CenterSettings.WidgetNotifyTestBuilds ? Core.Loc.T("Yes") : Core.Loc.T("No")));
        }

        private Border BuildWidgetNotifyRow(int index, string label, string value)
        {
            bool selected = index == _widgetNotifyRowIndex;

            var inner = new StackPanel();
            inner.Children.Add(new TextBlock
            {
                Text = Core.Loc.T(label),
                FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = UiHelpers.Text,
                TextWrapping = TextWrapping.Wrap,
            });
            inner.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 13, Foreground = UiHelpers.Subtle, Margin = new Thickness(0, 3, 0, 0),
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
                Child = inner,
            };
            card.MouseLeftButtonUp += (_, __) => { _widgetNotifyRowIndex = index; CycleWidgetNotifyRow(); };
            return card;
        }

        private void CycleWidgetNotifyRow()
        {
            if (_widgetNotifyRowIndex == 0)
                Core.CenterSettings.WidgetUpdateNotifyIntervalWeeks =
                    NextInterval(Core.CenterSettings.WidgetUpdateNotifyIntervalWeeks);
            else
                Core.CenterSettings.WidgetNotifyTestBuilds = !Core.CenterSettings.WidgetNotifyTestBuilds;

            RenderWidgetNotifySettings();
            RefreshActionBar();
        }

        private void MoveWidgetNotifySelection(PadButton dir)
        {
            int next = _widgetNotifyRowIndex;
            if (dir == PadButton.Up) next--;
            else if (dir == PadButton.Down) next++;
            else return;

            if (next < 0) next = 0;
            if (next > 1) next = 1;
            if (next == _widgetNotifyRowIndex) return;

            _widgetNotifyRowIndex = next;
            RenderWidgetNotifySettings();
        }

        private void RefreshWidgetNotifyActionBar()
        {
            AddAction(PadButton.A, "Change", true, CycleWidgetNotifyRow);
            AddAction(PadButton.B, "Back", true, OpenBrowse);
            AddScrollHint();
        }
    }
}
