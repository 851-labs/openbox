using System;
using System.Collections.Generic;
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
using Windows.UI.Xaml.Media.Animation;
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
        private static readonly Duration MoveDuration = new Duration(TimeSpan.FromMilliseconds(160));
        private const double LiftedScale = 1.06;

        private readonly Dictionary<UIElement, List<Storyboard>> _animations = new Dictionary<UIElement, List<Storyboard>>();
        private AppEntry _moving;
        private int _moveStartIndex;
        private List<AppEntry> _order;
        private Point[] _slots;
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

        // Moving a tile. Until it's put down the grid itself doesn't change: each tile is only
        // drawn somewhere else (a render transform, animated), so nothing is laid out or redrawn.
        // _order is where the tiles are shown, _slots where each place in the grid is.

        private void StartMove(AppEntry app)
        {
            Tiles.UpdateLayout();
            _slots = new Point[_apps.Count];
            for (var i = 0; i < _apps.Count; i++)
            {
                var container = Tiles.ContainerFromIndex(i) as UIElement;
                _slots[i] = container != null ? container.TransformToVisual(Tiles).TransformPoint(new Point()) : new Point();
            }
            _order = new List<AppEntry>(_apps);
            _moving = app;
            _moveStartIndex = _apps.IndexOf(app);
            var lifted = Container(app);
            if (lifted != null)
            {
                // Lifted: a little bigger, above the others, with a blue outline.
                lifted.FocusVisualPrimaryBrush = MovingFocusBrush;
                Canvas.SetZIndex(lifted, 1);
                Animate(lifted, 0, 0, LiftedScale);
            }
            Hints.Show("DPad", "Move " + app.Name, "A", "Done", "B", "Cancel");
        }

        private void MoveBy(int delta)
        {
            var from = _order.IndexOf(_moving);
            var to = from + delta;
            if (to < 0 || to >= _order.Count)
            {
                // Down from a row with nothing under the tile goes to the end; otherwise stay.
                to = delta > 1 && from < _order.Count - 1 ? _order.Count - 1 : from;
            }
            if (to == from)
            {
                return;
            }
            _order.RemoveAt(from);
            _order.Insert(to, _moving);
            // Each tile slides from where it is drawn to its place in the new order.
            for (var slot = 0; slot < _order.Count; slot++)
            {
                var app = _order[slot];
                var home = _apps.IndexOf(app);
                var container = Container(app);
                if (container != null)
                {
                    Animate(container, _slots[slot].X - _slots[home].X, _slots[slot].Y - _slots[home].Y,
                        app == _moving ? LiftedScale : 1);
                }
            }
        }

        private void EndMove(bool keep)
        {
            var app = _moving;
            var order = _order;
            _moving = null;
            _order = null;
            var lifted = Container(app);
            if (keep && order.IndexOf(app) != _moveStartIndex)
            {
                // Now the grid takes the new order, and the tiles are drawn where they are laid
                // out. Both happen before the next frame, so the screen doesn't change.
                foreach (var tile in _apps)
                {
                    var container = Container(tile);
                    if (container != null)
                    {
                        StopAnimations(container);
                    }
                }
                Shift(_moveStartIndex, order.IndexOf(app));
                AppLibrary.SaveOrder(_apps);
            }
            else
            {
                // Everything slides back.
                foreach (var tile in _apps)
                {
                    var container = Container(tile);
                    if (container != null && container != lifted)
                    {
                        Animate(container, 0, 0, 1);
                    }
                }
            }
            if (lifted != null)
            {
                lifted.ClearValue(FrameworkElement.FocusVisualPrimaryBrushProperty);
                Canvas.SetZIndex(lifted, 0);
                Animate(lifted, 0, 0, 1);
            }
            Hints.Show("A", "Open", "X", "Options");
            FocusTile(_apps.IndexOf(app));
        }

        /// <summary>
        /// Puts the tile at <paramref name="from"/> at <paramref name="to"/> by moving the tiles
        /// between, one place each: the moved tile itself is never taken out of the grid, so it
        /// keeps its container and its focus.
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

        private GridViewItem Container(AppEntry app)
        {
            return Tiles.ContainerFromItem(app) as GridViewItem;
        }

        private static CompositeTransform Transform(UIElement container)
        {
            var transform = container.RenderTransform as CompositeTransform;
            if (transform == null)
            {
                transform = new CompositeTransform();
                container.RenderTransform = transform;
                container.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            return transform;
        }

        // Slides and scales a tile to where it should be drawn, from wherever it is now: a new
        // animation takes over from the one running.
        private void Animate(UIElement container, double x, double y, double scale)
        {
            var transform = Transform(container);
            var storyboard = new Storyboard();
            foreach (var target in new[] { Tuple.Create("TranslateX", x), Tuple.Create("TranslateY", y),
                Tuple.Create("ScaleX", scale), Tuple.Create("ScaleY", scale) })
            {
                var animation = new DoubleAnimation
                {
                    To = target.Item2,
                    Duration = MoveDuration,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, transform);
                Storyboard.SetTargetProperty(animation, target.Item1);
                storyboard.Children.Add(animation);
            }
            List<Storyboard> running;
            if (!_animations.TryGetValue(container, out running))
            {
                _animations[container] = running = new List<Storyboard>();
            }
            running.Add(storyboard);
            storyboard.Begin();
        }

        // Stops all of a tile's animations, which puts it back where it's laid out.
        private void StopAnimations(UIElement container)
        {
            List<Storyboard> running;
            if (_animations.TryGetValue(container, out running))
            {
                running.ForEach(storyboard => storyboard.Stop());
                _animations.Remove(container);
            }
        }

        private void FocusTile(int index)
        {
            if (index < 0)
            {
                return;
            }
            Tiles.UpdateLayout();
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
            var columns = 1;
            while (columns < _slots.Length && Math.Abs(_slots[columns].Y - _slots[0].Y) < 1)
            {
                columns++;
            }
            return columns;
        }
    }
}
