using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace OpenBox.Controls
{
    /// <summary>
    /// What the controller's buttons do here, drawn the way the console does: A green, B red,
    /// X blue, Y yellow, and View, Menu and the D-pad as dark buttons with their symbols.
    /// </summary>
    public sealed class HintBar : StackPanel
    {
        private const double Size = 22;

        public HintBar()
        {
            Orientation = Orientation.Horizontal;
            Spacing = 20;
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Bottom;
        }

        /// <summary>Pairs of a button ("A", "B", "X", "Y", "View", "Menu", "DPad") and what it does.</summary>
        public void Show(params string[] hints)
        {
            Children.Clear();
            for (var i = 0; i + 1 < hints.Length; i += 2)
            {
                var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                hint.Children.Add(Button(hints[i]));
                hint.Children.Add(new TextBlock
                {
                    Text = hints[i + 1],
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                });
                Children.Add(hint);
            }
        }

        private static FrameworkElement Button(string name)
        {
            Color color;
            var dark = Color.FromArgb(255, 58, 58, 64);
            switch (name)
            {
                case "A": color = Color.FromArgb(255, 16, 124, 16); break;
                case "B": color = Color.FromArgb(255, 209, 52, 56); break;
                case "X": color = Color.FromArgb(255, 0, 103, 192); break;
                case "Y": color = Color.FromArgb(255, 255, 185, 0); break;
                default: color = dark; break;
            }
            var button = new Grid { Width = Size, Height = Size };
            button.Children.Add(new Ellipse { Fill = new SolidColorBrush(color) });
            var white = new SolidColorBrush(Colors.White);
            if (name == "View")
            {
                // Two overlapping squares.
                button.Children.Add(new Rectangle { Width = 7, Height = 7, Stroke = white, StrokeThickness = 1.2, RenderTransform = new TranslateTransform { X = -2, Y = -2 } });
                button.Children.Add(new Rectangle { Width = 7, Height = 7, Stroke = white, StrokeThickness = 1.2, Fill = new SolidColorBrush(dark), RenderTransform = new TranslateTransform { X = 2, Y = 2 } });
            }
            else if (name == "Menu")
            {
                // Three lines.
                var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                for (var i = 0; i < 3; i++)
                {
                    lines.Children.Add(new Rectangle { Width = 10, Height = 1.5, Fill = white });
                }
                button.Children.Add(lines);
            }
            else if (name == "DPad")
            {
                button.Children.Add(new Rectangle { Width = 4, Height = 12, Fill = white });
                button.Children.Add(new Rectangle { Width = 12, Height = 4, Fill = white });
            }
            else
            {
                button.Children.Add(new TextBlock
                {
                    Text = name,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    // Dark on yellow, as on the controller's Y.
                    Foreground = name == "Y" ? new SolidColorBrush(Color.FromArgb(255, 30, 30, 30)) : white,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 1),
                });
            }
            return button;
        }
    }
}
