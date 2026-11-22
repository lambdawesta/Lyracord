using Unicord.Universal.Extensions;
using Unicord.Universal.Pages;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unicord.Universal.Services
{
    public enum ViewMode
    {
        SinglePaneLeft,
        SinglePaneCentre,
        SinglePaneRight,
        TwoPaneLeft,
        TwoPaneRight,
        ThreePane
    }

    internal class SplitPaneService : BaseService<SplitPaneService>
    {
        // <= this point, show a single pane
        public const int TWO_PANE_BREAKPOINT = 767;
        // <= this point, show the two panes
        public const int THREE_PANE_BREAKPOINT = 1279;

        private DiscordPage _discordPage;
        private bool _isRightOpen;
        private bool _isLeftOpen;

        public ViewMode ViewMode { get; private set; } = (ViewMode)(-1);

        protected override void Initialise()
        {
            base.Initialise();

            _discordPage = Window.Current.Content.FindChild<DiscordPage>();
            if (_discordPage != null)
                Window.Current.SizeChanged += OnSizeChanged;
        }

        private void OnSizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            AdjustSize();
        }

        /// <summary>
        /// The Metro theme has its own pages for servers and channels: the old Unicord left pane (opened by the
        /// "three lines" button in a chat header) must not appear there. Opens the Metro page instead: the server of
        /// the open channel, or the servers list.
        /// </summary>
        private bool OpenMetroInstead()
        {
            if (Themes.ThemeResources.LoadedTheme != AppTheme.OLED || _discordPage == null)
                return false;
            try
            {
                var frame = _discordPage.MainFrame;
                var guild = (frame.Content as ChannelPage)?.ViewModel?.Channel?.Guild;
                if (guild != null)
                    frame.Navigate(typeof(GuildHubPage), guild);
                else
                    frame.Navigate(typeof(ServersHubPage));
            }
            catch (System.Exception ex)
            {
                VoiceDiagnostics.Log("Боковая панель (Metro)", ex);
            }
            return true;
        }

        internal void ToggleLeftPane()
        {
            if (!_isLeftOpen && OpenMetroInstead())
                return;

            if (_isLeftOpen)
            {
                CloseLeftPane();
            }
            else
            {
                OpenLeftPane();
            }

            AdjustSize();
        }

        internal void ToggleRightPane<T>(object param) where T : Page
        {
            if (_isRightOpen)
            {
                if (_discordPage.RightSidebarFrame.SourcePageType == typeof(T))
                {
                    CloseRightPane();
                }
                else
                {
                    _discordPage.RightSidebarFrame.Navigate(typeof(T), param);
                }
            }
            else
            {
                OpenRightPane();
                _discordPage.RightSidebarFrame.Navigate(typeof(T), param);
            }

            AdjustSize();
        }

        internal void AdjustSize()
        {
            var previousViewMode = ViewMode;

            ViewMode = (Window.Current.Bounds.Width) switch
            {
                > THREE_PANE_BREAKPOINT => _isRightOpen ? ViewMode.ThreePane : ViewMode.TwoPaneLeft,
                > TWO_PANE_BREAKPOINT => _isRightOpen ? ViewMode.TwoPaneRight : ViewMode.TwoPaneLeft,
                _ => _isRightOpen ? ViewMode.SinglePaneRight : _isLeftOpen && (previousViewMode != ViewMode.TwoPaneLeft) ? ViewMode.SinglePaneLeft : ViewMode.SinglePaneCentre
            };

            // ensure these sync
            if (ViewMode == ViewMode.SinglePaneCentre)
            {
                _isLeftOpen = false;
                _isRightOpen = false;
            }

            if (ViewMode == ViewMode.TwoPaneLeft || ViewMode == ViewMode.SinglePaneLeft)
            {
                _isLeftOpen = true;
                _isRightOpen = false;
            }

            if (ViewMode == ViewMode.TwoPaneRight || ViewMode == ViewMode.SinglePaneRight)
            {
                _isRightOpen = true;
                _isLeftOpen = false;
            }

            if (ViewMode == ViewMode.ThreePane)
            {
                _isLeftOpen = true;
                _isRightOpen = true;
            }

            if (_discordPage != null && previousViewMode != ViewMode)
                _discordPage.SetViewMode(ViewMode);
        }

        /// <summary>Shows the channel list (left pane); on phones it covers the main content.</summary>
        public void ShowLeftPane()
        {
            if (OpenMetroInstead())
                return;
            CloseRightPane();
            OpenLeftPane();
            AdjustSize();
        }

        public void CloseAllPanes()
        {
            CloseRightPane();
            CloseLeftPane();

            AdjustSize();
        }

        private void OpenLeftPane()
        {
            _isLeftOpen = true;
        }

        private void CloseLeftPane()
        {
            _isLeftOpen = false;
        }

        private void OpenRightPane()
        {
            _isRightOpen = true;
        }

        private void CloseRightPane()
        {
            _isRightOpen = false;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_isRightOpen)
                CloseRightPane();
        }
    }
}
