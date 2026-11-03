using System;
using System.Collections.ObjectModel;
using OpenBox.Apps;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace OpenBox.Home
{
    /// <summary>
    /// The homescreen: a tile for each app. A opens it; View in an app comes back here. X opens a
    /// tile's options: Move, which picks the tile up so the D-pad moves it around (A puts it
    /// down, B puts it back), and Uninstall for store apps.
    /// </summary>
    public sealed partial class HomePage : Page, IBackHandler
    {
        private static readonly Brush MovingFocusBrush = new SolidColorBrush(Color.FromArgb(255, 61, 139, 255));

        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private ObservableCollection<AppEntry> _apps;
        private int _focusedIndex;
        private AppEntry _moving;
        private int _moveStartIndex;
        // The press that put a tile down, or opened the menu, shouldn't also act when it's released.
        private VirtualKey? _swallowKeyUp;

        public HomePage()
        {
            this.InitializeComponent();
            _clock.Tick += (s, e) => UpdateClock();
            Hints.Show("A", "Open", "X", "Options");
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UpdateClock();
            _clock.Start();
            // Store apps may have been added or removed since last time.
            _apps = new ObservableCollection<AppEntry>(await AppLibrary.GetHomeAppsAsync());
            Tiles.ItemsSource = _apps;
            FocusTile(Math.Min(_focusedIndex, _apps.Count - 1));
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _clock.Stop();
            if (_moving != null)
            {
                EndMove(false);
            }
        }

        private void UpdateClock()
        {
            Clock.Text = DateTime.Now.ToString("t");
        }

        private void Tiles_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_moving != null)
            {
                return;
            }
            var app = (AppEntry)e.ClickedItem;
            _focusedIndex = Math.Max(0, _apps.IndexOf(app));
            App.Open(AppLibrary.Latest(app));
        }

        bool IBackHandler.HandleBack()
        {
            if (_moving == null)
            {
                return false;
            }
            EndMove(false);
            return true;
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_moving != null)
            {
                e.Handled = true;
                switch (e.Key)
                {
                    case VirtualKey.GamepadDPadLeft:
                    case VirtualKey.GamepadLeftThumbstickLeft:
                    case VirtualKey.Left:
                        MoveBy(-1);
                        break;
                    case VirtualKey.GamepadDPadRight:
                    case VirtualKey.GamepadLeftThumbstickRight:
                    case VirtualKey.Right:
                        MoveBy(1);
                        break;
                    case VirtualKey.GamepadDPadUp:
                    case VirtualKey.GamepadLeftThumbstickUp:
                    case VirtualKey.Up:
                        MoveBy(-Columns());
                        break;
                    case VirtualKey.GamepadDPadDown:
                    case VirtualKey.GamepadLeftThumbstickDown:
                    case VirtualKey.Down:
                        MoveBy(Columns());
                        break;
                    case VirtualKey.GamepadA:
                    case VirtualKey.Enter:
                    case VirtualKey.Space:
                        _swallowKeyUp = e.Key;
                        EndMove(true);
                        break;
                    case VirtualKey.GamepadB:
                    case VirtualKey.Escape:
                        _swallowKeyUp = e.Key;
                        EndMove(false);
                        break;
                }
                return;
            }
            if (e.Key == VirtualKey.GamepadX)
            {
                var container = FocusedTile();
                if (container != null)
                {
                    e.Handled = true;
                    _swallowKeyUp = e.Key;
                    ShowOptions(container);
                }
            }
        }

        private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (_swallowKeyUp == e.Key)
            {
                _swallowKeyUp = null;
                e.Handled = true;
            }
        }

        private void ShowOptions(GridViewItem container)
        {
            var app = (AppEntry)Tiles.ItemFromContainer(container);
            var move = new MenuFlyoutItem { Text = "Move" };
            move.Click += (s, e) => StartMove(app);
            // Built-in apps are part of openbox: they can be moved, not removed.
            var uninstall = new MenuFlyoutItem { Text = "Uninstall", IsEnabled = !app.IsBuiltIn };
            uninstall.Click += (s, e) => Uninstall(app);
            var menu = new MenuFlyout();
            menu.Items.Add(move);
            menu.Items.Add(uninstall);
            menu.ShowAt(container, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        }

        private async void Uninstall(AppEntry app)
        {
            var index = _apps.IndexOf(app);
            _apps.Remove(app);
            FocusTile(Math.Min(index, _apps.Count - 1));
            await AppLibrary.UninstallAsync(app.Id);
        }

        private void StartMove(AppEntry app)
        {
            _moving = app;
            _moveStartIndex = _apps.IndexOf(app);
            Hints.Show("DPad", "Move " + app.Name, "A", "Done", "B", "Cancel");
            FocusTile(_moveStartIndex);
        }

        private void MoveBy(int delta)
        {
            var from = _apps.IndexOf(_moving);
            var to = from + delta;
            if (to < 0 || to >= _apps.Count)
            {
                // Down from a row with nothing under the tile goes to the end; otherwise stay.
                to = delta > 1 && from < _apps.Count - 1 ? _apps.Count - 1 : from;
            }
            Shift(from, to);
            FocusTile(to);
        }

        /// <summary>
        /// Puts the lifted tile at <paramref name="to"/> by moving the tiles between, one place
        /// each: the lifted tile itself is never taken out of the grid, so it keeps its focus and
        /// nothing else on the screen redraws.
        /// </summary>
        private void Shift(int from, int to)
        {
            for (; from < to; from++)
            {
                _apps.Move(from + 1, from);
            }
            for (; from > to; from--)
            {
                _apps.Move(from - 1, from);
            }
        }

        private void EndMove(bool keep)
        {
            var app = _moving;
            _moving = null;
            var index = _apps.IndexOf(app);
            if (!keep && index != _moveStartIndex)
            {
                Shift(index, _moveStartIndex);
                index = _moveStartIndex;
            }
            if (keep)
            {
                AppLibrary.SaveOrder(_apps);
            }
            Hints.Show("A", "Open", "X", "Options");
            FocusTile(index);
        }

        /// <summary>Focuses a tile, and shows which one is being moved, if any.</summary>
        private void FocusTile(int index)
        {
            if (index < 0)
            {
                return;
            }
            Tiles.UpdateLayout();
            for (var i = 0; i < _apps.Count; i++)
            {
                var container = Tiles.ContainerFromIndex(i) as GridViewItem;
                if (container == null)
                {
                    continue;
                }
                if (_apps[i] == _moving)
                {
                    // Lifted: bigger, with a blue outline.
                    container.RenderTransformOrigin = new Point(0.5, 0.5);
                    container.RenderTransform = new ScaleTransform { ScaleX = 1.06, ScaleY = 1.06 };
                    container.FocusVisualPrimaryBrush = MovingFocusBrush;
                    Canvas.SetZIndex(container, 1);
                }
                else
                {
                    container.ClearValue(UIElement.RenderTransformProperty);
                    container.ClearValue(FrameworkElement.FocusVisualPrimaryBrushProperty);
                    Canvas.SetZIndex(container, 0);
                }
            }
            (Tiles.ContainerFromIndex(index) as Control)?.Focus(FocusState.Keyboard);
        }

        private GridViewItem FocusedTile()
        {
            for (var element = FocusManager.GetFocusedElement() as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
            {
                var container = element as GridViewItem;
                if (container != null)
                {
                    return container;
                }
            }
            return null;
        }

        /// <summary>How many tiles fit across, for moving one up or down a row.</summary>
        private int Columns()
        {
            var first = Tiles.ContainerFromIndex(0) as UIElement;
            if (first == null)
            {
                return 1;
            }
            var top = first.TransformToVisual(Tiles).TransformPoint(new Point()).Y;
            var columns = 1;
            for (var i = 1; i < _apps.Count; i++)
            {
                var container = Tiles.ContainerFromIndex(i) as UIElement;
                if (container == null || Math.Abs(container.TransformToVisual(Tiles).TransformPoint(new Point()).Y - top) > 1)
                {
                    break;
                }
                columns++;
            }
            return columns;
        }
    }
}
