using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Shapes;

namespace Unicord.Universal.Controls
{
    /// <summary>
    /// Metro-style indeterminate loader: a short "snake" of small blue squares runs along a row, with a fading tail.
    /// Opacity key frames only, so the animation runs on the compositor and stays smooth while the UI thread is busy.
    /// </summary>
    public sealed class SnakeLoader : UserControl
    {
        private const int Cells = 8;
        private const int Tail = 4;
        private const int Pause = 4; // empty steps before the snake starts again
        private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(90);
        private static readonly double[] TailOpacity = { 1.0, 0.7, 0.45, 0.2 };

        private readonly Storyboard _storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        private readonly StackPanel _row = new StackPanel { Orientation = Orientation.Horizontal };
        private bool _running;

        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(SnakeLoader), new PropertyMetadata(false, (d, e) => ((SnakeLoader)d).UpdateState()));

        public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
            nameof(CellSize), typeof(double), typeof(SnakeLoader), new PropertyMetadata(8.0, (d, e) => ((SnakeLoader)d).Build()));

        public static readonly DependencyProperty CellBrushProperty = DependencyProperty.Register(
            nameof(CellBrush), typeof(Brush), typeof(SnakeLoader), new PropertyMetadata(null, (d, e) => ((SnakeLoader)d).Build()));

        public SnakeLoader()
        {
            IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            Content = _row;
            Build();
            Loaded += (s, e) => UpdateState();
            Unloaded += (s, e) => StopAnimation();
        }

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public double CellSize
        {
            get => (double)GetValue(CellSizeProperty);
            set => SetValue(CellSizeProperty, value);
        }

        /// <summary>Square colour; Windows Phone blue when not set.</summary>
        public Brush CellBrush
        {
            get => (Brush)GetValue(CellBrushProperty);
            set => SetValue(CellBrushProperty, value);
        }

        private void Build()
        {
            var wasRunning = _running;
            StopAnimation();
            _storyboard.Children.Clear();
            _row.Children.Clear();

            var size = CellSize;
            var brush = CellBrush ?? new SolidColorBrush(Color.FromArgb(255, 0x1B, 0xA1, 0xE2));
            var cycle = TimeSpan.FromTicks(Step.Ticks * (Cells + Pause));

            for (var i = 0; i < Cells; i++)
            {
                var cell = new Rectangle
                {
                    Width = size,
                    Height = size,
                    Fill = brush,
                    Opacity = 0,
                    Margin = new Thickness(i == 0 ? 0 : size / 2, 0, 0, 0)
                };
                _row.Children.Add(cell);

                // cell i is the head at step i, then fades out over the following Tail-1 steps
                var animation = new DoubleAnimationUsingKeyFrames { Duration = cycle };
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
                for (var k = 0; k < Tail; k++)
                    animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromTicks(Step.Ticks * (i + k)), Value = TailOpacity[k] });
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromTicks(Step.Ticks * (i + Tail)), Value = 0 });

                Storyboard.SetTarget(animation, cell);
                Storyboard.SetTargetProperty(animation, "Opacity");
                _storyboard.Children.Add(animation);
            }

            if (wasRunning)
                UpdateState();
        }

        private void UpdateState()
        {
            if (IsActive)
            {
                Visibility = Visibility.Visible;
                if (!_running)
                {
                    _running = true;
                    try { _storyboard.Begin(); } catch (Exception) { _running = false; }
                }
            }
            else
            {
                StopAnimation();
                Visibility = Visibility.Collapsed;
            }
        }

        private void StopAnimation()
        {
            if (!_running)
                return;
            _running = false;
            try { _storyboard.Stop(); } catch (Exception) { }
        }
    }
}
