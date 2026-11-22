using System;
using System.Collections.Generic;
using Unicord.Universal.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// Windows Phone page animations for the Metro theme: the "turnstile" (a page swings in around the left edge of the
    /// screen, like a door) and the "feather" (tiles and list rows swing in one after another).
    /// Smoother than on Windows Phone 8.1: the old page is not animated out first (the new one starts at once), the
    /// swing slows down towards the end (exponential ease) instead of moving at a constant speed, and only rotation
    /// and opacity are animated, which run on the compositor and keep moving while the page is still being built.
    /// </summary>
    internal static class MetroTransitions
    {
        public const string SettingKey = "MetroAnimations";

        private const int PageMs = 360;
        private const int ItemMs = 380;
        private const double PageAngle = -62;  // forward: the page swings in from beyond the left edge
        private const double BackAngle = 48;   // back: from the other side, a little shorter
        private const double ItemAngle = -72;

        /// <summary>On in the Metro theme unless turned off in the settings or in Windows (Ease of Access → animations).</summary>
        public static bool Enabled
        {
            get
            {
                try
                {
                    if (Themes.ThemeResources.LoadedTheme != AppTheme.OLED)
                        return false;
                    if (!App.LocalSettings.Read(SettingKey, true))
                        return false;
                    return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Turnstile for a page that has just been navigated to.</summary>
        public static void PageIn(FrameworkElement page, NavigationMode mode)
        {
            if (page == null || !Enabled)
                return;
            var back = mode == NavigationMode.Back;
            Swing(page, back ? BackAngle : PageAngle, PageMs, 0, 0);
        }

        /// <summary>
        /// Feather: the visible children of a panel swing in one by one, all around the left edge of the screen (items
        /// further right travel further, like on the phone). Call after the children are laid out.
        /// </summary>
        public static void Feather(Panel panel, int startDelayMs = 40, int stepMs = 32, int max = 14)
        {
            if (panel == null)
                return;
            var items = new List<FrameworkElement>();
            var count = 0;
            foreach (var child in panel.Children)
            {
                if (count++ >= max)
                    break;
                if (Enabled && child is FrameworkElement element && element.Visibility == Visibility.Visible && element.ActualHeight > 0)
                    items.Add(element);
                else
                    child.Opacity = 1; // hidden for the feather but not animated: show it
            }
            Feather(items, panel, startDelayMs, stepMs, max);
        }

        public static void Feather(IList<FrameworkElement> items, UIElement hinge, int startDelayMs = 40, int stepMs = 32, int max = 14)
        {
            if (items == null || !Enabled)
                return;
            var root = Window.Current?.Content;
            var count = 0;
            foreach (var element in items)
            {
                if (count >= max)
                    break;
                // the hinge is the left edge of the screen: as far left of this element as it is from that edge
                double centre = 0;
                try
                {
                    if (root != null && element.ActualWidth > 0)
                    {
                        var x = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                        centre = -Math.Max(0, x) / element.ActualWidth;
                    }
                }
                catch (Exception) { }
                Swing(element, ItemAngle, ItemMs, startDelayMs + count * stepMs, centre);
                count++;
            }
        }

        /// <summary>Feather for a panel once its new children have been laid out.</summary>
        public static void FeatherAfterLayout(Panel panel, int startDelayMs = 40, int stepMs = 32, int max = 14)
        {
            if (panel == null || !Enabled)
                return;
            HideForFeather(panel, max);
            _ = panel.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => Feather(panel, startDelayMs, stepMs, max));
        }

        // until the feather starts, the first items must not flash at their final place
        private static void HideForFeather(Panel panel, int max)
        {
            var count = 0;
            foreach (var child in panel.Children)
            {
                if (count++ >= max)
                    break;
                child.Opacity = 0;
            }
        }

        private static void Swing(FrameworkElement element, double fromAngle, int durationMs, int delayMs, double centreX)
        {
            try
            {
                // an element with its own projection (the flipping friends tile) keeps it; otherwise a temporary one
                var own = element.Projection as PlaneProjection;
                var projection = own ?? new PlaneProjection();
                projection.CenterOfRotationX = own != null ? projection.CenterOfRotationX : centreX;
                projection.RotationY = fromAngle;
                if (own == null)
                    element.Projection = projection;

                element.Opacity = 0;

                var storyboard = new Storyboard();
                var rotate = new DoubleAnimation
                {
                    From = fromAngle,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                    EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 }
                };
                Storyboard.SetTarget(rotate, projection);
                Storyboard.SetTargetProperty(rotate, "RotationY");
                storyboard.Children.Add(rotate);

                var fade = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(durationMs * 0.45),
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                };
                Storyboard.SetTarget(fade, element);
                Storyboard.SetTargetProperty(fade, "Opacity");
                storyboard.Children.Add(fade);

                storyboard.Completed += (s, a) =>
                {
                    element.Opacity = 1;
                    projection.RotationY = 0;
                    storyboard.Stop();
                    if (own == null)
                        element.Projection = null; // no 3D layer once still: cheaper to draw
                };
                storyboard.Begin();
            }
            catch (Exception)
            {
                element.Opacity = 1;
            }
        }
    }
}
