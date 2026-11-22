using System;
using System.Threading.Tasks;
using Unicord.Universal.Dialogs;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml.Controls;

namespace Unicord.Universal.Utilities
{
    internal static class UIUtilities
    {
        private static Lazy<ResourceLoader> _resources
            = new Lazy<ResourceLoader>(() => ResourceLoader.GetForViewIndependentUse("Dialogs"));

        /// <summary>
        /// On the phone the app draws under the system bars (UseCoreWindow) and keeps its pages clear of them with
        /// margins; a dialog lives in its own layer and is not affected, so a tall one put its buttons behind the
        /// navigation bar. While such a dialog is open, the window is limited to the visible area.
        /// </summary>
        public static void KeepClearOfSystemBars(ContentDialog dialog)
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
                return; // desktop: no system bars over the window

            // Switch to UseVisible BEFORE ShowAsync() measures the popup, AND apply an explicit bottom margin
            // because on Windows 10 Mobile 15254 switching bounds in Opened alone happens after layout and
            // leaves tall dialogs (like TokenDialog) with their buttons behind the on-screen navigation bar.
            double topInset = 0;
            double bottomInset = 0;
            try
            {
                var view = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();
                var bounds = Windows.ApplicationModel.Core.CoreApplication.GetCurrentView().CoreWindow.Bounds;
                var visible = view.VisibleBounds;
                topInset = Math.Max(0, visible.Top - bounds.Top);
                bottomInset = Math.Max(0, bounds.Bottom - visible.Bottom);
            }
            catch (Exception) { }

            dialog.Margin = new Windows.UI.Xaml.Thickness(0, topInset, 0, Math.Max(72, bottomInset + 24));
            SetBounds(Windows.UI.ViewManagement.ApplicationViewBoundsMode.UseVisible);
            dialog.Opened += (s, e) => SetBounds(Windows.UI.ViewManagement.ApplicationViewBoundsMode.UseVisible);
            dialog.Closed += (s, e) => SetBounds(Windows.UI.ViewManagement.ApplicationViewBoundsMode.UseCoreWindow);
        }

        private static void SetBounds(Windows.UI.ViewManagement.ApplicationViewBoundsMode mode)
        {
            try { Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().SetDesiredBoundsMode(mode); }
            catch (Exception) { }
        }

        public static async Task ShowErrorDialogAsync(string title, string content, string icon = null, string resourceMap = null)
        {
            try
            {
                var resources = resourceMap != null ? ResourceLoader.GetForViewIndependentUse(resourceMap) : _resources.Value;   
                
                var actualTitle = resources.GetString(title);
                var actualContent = resources.GetString(content);

                var dialog = new ErrorDialog()
                {
                    Title = string.IsNullOrWhiteSpace(actualTitle) ? title : actualTitle,
                    Content = string.IsNullOrWhiteSpace(actualContent) ? content : actualContent
                };

                if (icon != null)
                {
                    dialog.Icon = icon;
                }

                await dialog.ShowAsync();
            }
            catch { }
        }

        public static async Task<bool> ShowYesNoDialogAsync(string title, string content, string icon = null)
        {
            try
            {
                var dialog = new ConfirmationDialog() { Title = title, Content = content };

                if (icon != null)
                {
                    dialog.Icon = icon;
                }

                return await dialog.ShowAsync() == ContentDialogResult.Primary ? true : false;
            }
            catch
            {
                return false;
            }
        }
    }
}
