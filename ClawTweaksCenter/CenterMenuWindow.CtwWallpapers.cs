using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClawTweaksCenter.Library;
using ClawTweaksCenter.Navigation;
using ClawTweaksCenter.Ui;

namespace ClawTweaksCenter
{
    /// <summary>
    /// The wallpapers that come with ClawTweaks, as one more page of the background picker.
    ///
    /// It is reached with Y FROM the user's own pictures (CenterMenuWindow.UserArt.cs) and goes back
    /// there with B, because it is the same question - "what goes behind the window" - answered from
    /// a different shelf. It took Y from "Rescan" on the user's request (2026-09-13): a folder listing
    /// that is out of date is already one B and one A away from being read again, so the button was
    /// spent on the cheaper of the two.
    ///
    /// Tiles ARE the download. Each file is a megabyte and a half, so there is nothing smaller to
    /// fetch first - a thumbnail set would be a second thing in the repo that can fall out of step
    /// with the pictures it claims to show. They arrive one after another rather than all at once so
    /// the first tile fills in while the rest are still coming, and the file stays cached, so the
    /// second visit draws immediately and picking one costs no download at all.
    /// </summary>
    public partial class CenterMenuWindow
    {
        private List<CtwWallpapers.Wallpaper> _ctwWallpapers;
        private readonly List<Border> _ctwWallpaperTiles = new List<Border>();
        private int _ctwWallpaperIndex;
        private ScrollViewer _ctwWallpaperScroller;
        private bool _ctwWallpapersLoading;
        private bool _ctwWallpapersLoaded;
        /// <summary>Set when the list itself could not be read - a different sentence from an empty
        /// list, and the two must not share one.</summary>
        private bool _ctwWallpapersOffline;
        private CancellationTokenSource _ctwWallpaperCts;

        #region Entry and exit
        private void OpenCtwWallpapers()
        {
            _gameMenuOverlay = GameMenuOverlay.CtwWallpapers;
            _ctwWallpapers = null;
            _ctwWallpapersLoaded = false;
            _ctwWallpapersOffline = false;
            _ctwWallpaperIndex = 0;
            RenderGameMenuOverlay();
            RefreshActionBar();
            StartCtwWallpaperLoad();
        }

        /// <summary>B: back to the user's own pictures, not out of the picker altogether. This page
        /// is a detour from that grid and leaving it should undo the detour.</summary>
        private void CtwWallpapersBack()
        {
            _ctwWallpaperCts?.Cancel();
            _ctwWallpaperCts = null;
            _ctwWallpaperTiles.Clear();
            _ctwWallpaperScroller = null;
            _ctwWallpapersLoading = false;

            // Straight back to the folder chooser when no folder has ever been named: the grid
            // behind this one would have nothing in it, and an empty screen is not a way back.
            _gameMenuOverlay = UserImageLibrary.HasFolder ? GameMenuOverlay.UserArt : GameMenuOverlay.UserArtFolder;
            RenderGameMenuOverlay();
            RefreshActionBar();
            if (_gameMenuOverlay == GameMenuOverlay.UserArt) StartUserArtScan();
        }
        #endregion

        #region Loading
        private void StartCtwWallpaperLoad()
        {
            _ctwWallpaperCts?.Cancel();
            _ctwWallpaperCts = new CancellationTokenSource();
            var ct = _ctwWallpaperCts.Token;

            _ctwWallpapersLoading = true;
            RenderGameMenuOverlay();
            RefreshActionBar();

            _ = Task.Run(async () =>
            {
                var list = await CtwWallpapers.ListAsync(ct).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || _gameMenuOverlay != GameMenuOverlay.CtwWallpapers) return;
                    _ctwWallpapers = list ?? new List<CtwWallpapers.Wallpaper>();
                    _ctwWallpapersOffline = list == null;
                    _ctwWallpapersLoading = false;
                    _ctwWallpapersLoaded = true;
                    _ctwWallpaperIndex = 0;
                    RenderGameMenuOverlay();
                    RefreshActionBar();
                });

                if (list == null) return;

                // One at a time, and each one paints as it lands. Firing all of them together would
                // hand the panel several megabytes of decoding at once for tiles nobody is looking
                // at yet, and the first row is the only one on screen anyway.
                foreach (var wp in list)
                {
                    if (ct.IsCancellationRequested) return;
                    string path = await CtwWallpapers.EnsureFileAsync(wp, ct).ConfigureAwait(false);
                    if (path == null) continue;
                    var bmp = await GameArt.LoadAsync(path, (int)UserArtBackgroundTileWidth * 2).ConfigureAwait(false);
                    if (bmp == null) continue;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || _gameMenuOverlay != GameMenuOverlay.CtwWallpapers) return;
                        foreach (var tile in _ctwWallpaperTiles)
                            if (tile.Child is Image img && ReferenceEquals(img.Tag, wp)) img.Source = bmp;
                    });
                }
            }, ct);
        }
        #endregion

        #region Rendering
        private void RenderCtwWallpapers()
        {
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            LibraryRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new StackPanel { Margin = new Thickness(LibOuterMargin, 14, LibOuterMargin, 10), MaxWidth = 900 };
            head.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("ClawTweaks wallpapers"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiHelpers.Text,
                Margin = new Thickness(0, 0, 0, 4),
            });
            head.Children.Add(new TextBlock
            {
                Text = Core.Loc.T("Downloaded when you pick one."),
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
            });
            Grid.SetRow(head, 0);
            LibraryRoot.Children.Add(head);

            UIElement body;
            if (_userArtApplying)
            {
                body = BuildLibraryMessage("Setting the picture\u2026", true);
            }
            else if (_ctwWallpapersLoading || !_ctwWallpapersLoaded)
            {
                body = BuildLibraryMessage("Looking for wallpapers\u2026", true);
            }
            else if (_ctwWallpapersOffline)
            {
                // Names the cause. "No wallpapers" on a machine that simply has no network reads as
                // the feature being gone.
                body = BuildLibraryMessage("The wallpaper list could not be reached. Check your connection.", false);
            }
            else if (_ctwWallpapers.Count == 0)
            {
                body = BuildLibraryMessage("No wallpapers published yet.", false);
            }
            else
            {
                var grid = new UniformGrid
                {
                    Columns = UserArtBackgroundColumns,
                    Margin = new Thickness(LibOuterMargin, 0, LibOuterMargin, 12),
                };
                for (int i = 0; i < _ctwWallpapers.Count; i++) grid.Children.Add(BuildCtwWallpaperTile(i));
                _ctwWallpaperScroller = new ScrollViewer
                {
                    Content = grid,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                };
                body = _ctwWallpaperScroller;
            }

            Grid.SetRow(body, 1);
            LibraryRoot.Children.Add(body);
            ApplyCtwWallpaperSelection();
        }

        private UIElement BuildCtwWallpaperTile(int index)
        {
            var wp = _ctwWallpapers[index];

            var image = new Image { Stretch = Stretch.UniformToFill, Tag = wp };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var tile = new Border
            {
                Width = UserArtBackgroundTileWidth,
                Height = UserArtBackgroundTileHeight,
                CornerRadius = new CornerRadius(6),
                Background = UiHelpers.Card,
                ClipToBounds = true,
                BorderThickness = new Thickness(3),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
                Child = image,
            };

            int captured = index;
            tile.MouseLeftButtonUp += (_, __) =>
            {
                _ctwWallpaperIndex = captured;
                ApplyCtwWallpaperSelection();
                ApplyPickedCtwWallpaper();
            };
            _ctwWallpaperTiles.Add(tile);

            // The name sits UNDER the picture rather than on it: these are near-full-bleed gradients,
            // and a caption on top of one is unreadable on whichever wallpaper happens to be bright.
            var cell = new StackPanel { Margin = new Thickness(6) };
            cell.Children.Add(tile);
            cell.Children.Add(new TextBlock
            {
                Text = wp.Name,
                FontSize = 12,
                Foreground = UiHelpers.Subtle,
                Margin = new Thickness(2, 5, 2, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return cell;
        }

        private void ApplyCtwWallpaperSelection()
        {
            foreach (var tile in _ctwWallpaperTiles)
                tile.BorderBrush = tile.Tag is int i && i == _ctwWallpaperIndex ? UiHelpers.Accent : Brushes.Transparent;
        }

        private void MoveCtwWallpaperSelection(PadButton dir)
        {
            if (_ctwWallpaperTiles.Count == 0) return;
            int columns = UserArtBackgroundColumns;
            int next = _ctwWallpaperIndex;
            switch (dir)
            {
                case PadButton.Left: if (next % columns == 0) return; next -= 1; break;
                case PadButton.Right:
                    if (next % columns == columns - 1 || next + 1 >= _ctwWallpaperTiles.Count) return;
                    next += 1;
                    break;
                case PadButton.Up:
                    if (next < columns) return;
                    next -= columns;
                    break;
                case PadButton.Down:
                    next += columns;
                    // Clamped into a short last row rather than refused, exactly as the picture grid
                    // next door does it - straight down from a full row is otherwise a dead end.
                    if (next >= _ctwWallpaperTiles.Count)
                    {
                        if (_ctwWallpaperIndex >= _ctwWallpaperTiles.Count - 1) return;
                        next = _ctwWallpaperTiles.Count - 1;
                    }
                    break;
                default: return;
            }
            _ctwWallpaperIndex = next;
            ApplyCtwWallpaperSelection();
            try { _ctwWallpaperTiles[_ctwWallpaperIndex].BringIntoView(); } catch { }
            RefreshActionBar();
        }
        #endregion

        #region Applying
        /// <summary>
        /// A, on a tile: makes sure the file is here and then hands it to the SAME code a picture of
        /// the user's own goes through.
        ///
        /// It has to be that code and not a shortcut of its own - ApplyPickedBackground is where the
        /// previous background is retired and where the copy that ends up in the art cache is made.
        /// A second route into "this is now the background" is a second opinion about what that means.
        /// </summary>
        private void ApplyPickedCtwWallpaper()
        {
            if (_userArtApplying) return;
            if (_ctwWallpapers == null) return;
            if (_ctwWallpaperIndex < 0 || _ctwWallpaperIndex >= _ctwWallpapers.Count) return;

            var wp = _ctwWallpapers[_ctwWallpaperIndex];
            _userArtApplying = true;
            RenderGameMenuOverlay();
            RefreshActionBar();

            var ct = _ctwWallpaperCts?.Token ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                string path = await CtwWallpapers.EnsureFileAsync(wp, ct).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    _userArtApplying = false;
                    if (path == null)
                    {
                        // Back to the grid with a working action bar. Silently doing nothing here is
                        // indistinguishable from the button not being wired up.
                        Core.InstallLog.Write("[Wallpapers] could not apply " + wp.Id + " - the download failed");
                        RenderGameMenuOverlay();
                        RefreshActionBar();
                        return;
                    }

                    // The purpose has to be right before the shared code runs: it is what decides
                    // this becomes the background rather than a cover for whatever game was last
                    // open in the menu.
                    _userArtPurpose = UserArtPurpose.Background;
                    ApplyPickedBackground(path);
                });
            }, ct);
        }
        #endregion

        #region Footer
        private bool RefreshCtwWallpaperActionBar()
        {
            if (_gameMenuOverlay != GameMenuOverlay.CtwWallpapers) return false;

            AddAction(PadButton.A, "Use as background",
                !_userArtApplying && !_ctwWallpapersLoading && _ctwWallpaperTiles.Count > 0, ApplyPickedCtwWallpaper);
            AddAction(PadButton.B, "Back", !_userArtApplying, CtwWallpapersBack);
            return true;
        }
        #endregion
    }
}
