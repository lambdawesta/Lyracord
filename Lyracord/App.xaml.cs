using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Web;
using DSharpPlus;
using DSharpPlus.Entities;
#if XBOX_GAME_BAR
using Microsoft.Gaming.XboxGameBar;
using Unicord.Universal.Pages.GameBar;
#endif
using Microsoft.Toolkit.Uwp.Helpers;
using Microsoft.Toolkit.Uwp.UI;
using Unicord.Universal.Extensions;
using Unicord.Universal.Integration;
using Unicord.Universal.Models;
using Unicord.Universal.Pages;
using Unicord.Universal.Services;
using Unicord.Universal.Utilities;
using WamWooWam.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Resources;
using Windows.Media.Transcoding;
using Windows.Security.Credentials;
using Windows.Storage;
using Windows.UI.Notifications;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using static Unicord.Constants;
using UnhandledExceptionEventArgs = Windows.UI.Xaml.UnhandledExceptionEventArgs;

namespace Unicord.Universal
{
    sealed partial class App : Application
    {
#if XBOX_GAME_BAR
        private static XboxGameBarWidget _chatListWidget;
        private static XboxGameBarWidget _friendsListWidget;
#endif
        internal static ApplicationDataStorageHelper LocalSettings { get; }
            = ApplicationDataStorageHelper.GetCurrent();
        internal static ApplicationDataStorageHelper RoamingSettings { get; }
            = ApplicationDataStorageHelper.GetCurrent();

        public App()
        {
            Services.PerfTimings.Init();
            Services.AppLanguageService.ApplySaved();
            InitializeComponent();
            MigratePreV2Settings();

            var theme = (ElementTheme)LocalSettings.Read(REQUESTED_COLOUR_SCHEME, (int)ElementTheme.Default);
            switch (theme)
            {
                case ElementTheme.Light:
                    RequestedTheme = ApplicationTheme.Light;
                    break;
                case ElementTheme.Dark:
                    RequestedTheme = ApplicationTheme.Dark;
                    break;
            }

            Suspending += OnSuspending;
            Resuming += OnResuming;
            EnteredBackground += OnEnteredBackground;
            LeavingBackground += OnLeavingBackground;
            UnhandledException += OnUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Services.VoiceDiagnostics.LogCrash("фоновая задача", e.Exception);
                e.SetObserved();
            };
            System.AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Services.VoiceDiagnostics.LogCrash(e.IsTerminating ? "процесс, завершение" : "процесс", e.ExceptionObject as Exception);
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                Services.VoiceDiagnostics.Log($"==== запуск Lyracord {v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
            }
            catch (Exception) { }

            Debug.WriteLine("Welcome to Lyracord!");
        }

        private static void MigratePreV2Settings()
        {
            if (LocalSettings.Read("PreV2SettingsMigrated", false))
                return;

            var localValues = ApplicationData.Current.LocalSettings.Values;
            foreach (var item in ApplicationData.Current.RoamingSettings.Values)
            {
                if (!localValues.ContainsKey(item.Key))
                    localValues[item.Key] = item.Value;
            }

            if (LocalSettings.TryRead<string>(AUTO_TRANSCODE_MEDIA_OLD, out var autoTranscodeMedia))
            {
                if (Enum.TryParse<MediaTranscodeOptions>(autoTranscodeMedia, out var result))
                    LocalSettings.Save(AUTO_TRANSCODE_MEDIA, (int)result);

                LocalSettings.TryDelete(AUTO_TRANSCODE_MEDIA_OLD);
            }

            if (LocalSettings.TryRead<string>(VIDEO_PROCESSING_OLD, out var videoProcessingOptions))
            {
                if (Enum.TryParse<MediaVideoProcessingAlgorithm>(videoProcessingOptions, out var result))
                    LocalSettings.Save(VIDEO_PROCESSING, (int)result);

                LocalSettings.TryDelete(VIDEO_PROCESSING_OLD);
            }

            if (LocalSettings.TryRead<string>(TIMESTAMP_STYLE_OLD, out var timestampStyle))
            {
                if (Enum.TryParse<TimestampStyle>(timestampStyle, out var result))
                    LocalSettings.Save(TIMESTAMP_STYLE, (int)result);

                LocalSettings.TryDelete(TIMESTAMP_STYLE_OLD);
            }

            if (LocalSettings.TryRead<string>(REQUESTED_COLOUR_SCHEME_OLD, out var requestedScheme))
            {
                if (Enum.TryParse<ElementTheme>(requestedScheme, out var result))
                    LocalSettings.Save(REQUESTED_COLOUR_SCHEME, (int)result);

                LocalSettings.TryDelete(REQUESTED_COLOUR_SCHEME_OLD);
            }

            try
            {
                var passwordVault = new PasswordVault();
                foreach (var c in passwordVault.FindAllByResource(TOKEN_IDENTIFIER_OLD))
                {
                    c.RetrievePassword();
                    passwordVault.Add(new PasswordCredential(TOKEN_IDENTIFIER, c.UserName, c.Password));

                    passwordVault.Remove(c);
                }
            }
            catch { }

            LocalSettings.Save("PreV2SettingsMigrated", true);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Services.VoiceDiagnostics.LogCrash("интерфейс", e.Exception);
            if (e.Exception == null || string.IsNullOrEmpty(e.Exception.StackTrace))
                Services.VoiceDiagnostics.Log("   сообщение: " + e.Message);
            Logger.LogError(e.Exception);
            Logger.Log(e.Message);
            e.Handled = true;
        }

        protected override async void OnActivated(IActivatedEventArgs e)
        {
            switch (e)
            {
                case ContactPanelActivatedEventArgs cont:
                    await OnContactPanelActivated(cont);
                    return;
                case ToastNotificationActivatedEventArgs toast:
                    OnLaunched(false, toast.Argument);
                    return;
                case ProtocolActivatedEventArgs protocol:
                    OnProtocolActivatedAsync(protocol);
                    return;
#if XBOX_GAME_BAR
                case XboxGameBarWidgetActivatedEventArgs xbox:
                    OnXboxGameBarActivated(xbox);
                    return;
#endif
                default:
                    Debug.WriteLine(e.Kind);
                    break;
            }
        }

        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            var deferral = args.TaskInstance.GetDeferral();

            switch (args.TaskInstance.Task.Name)
            {
                case TOAST_BACKGROUND_TASK_NAME:
                    if (args.TaskInstance.TriggerDetails is not ToastNotificationActionTriggerDetail details || !DiscordManager.TryGetToken(out var token))
                        break;

                    var arguments = ParseArgs(details.Argument);
                    var userInput = details.UserInput;

                    if (!arguments.TryGetValue("channelId", out var cId) || !ulong.TryParse(cId, out var channelId))
                        break;

                    if (!userInput.TryGetValue("tbReply", out var t) || t is not string text)
                        break;

                    var client = new DiscordRestClient(new DiscordConfiguration() { Token = token, TokenType = TokenType.User });
                    await client.CreateMessageAsync(channelId, new DiscordMessageBuilder().WithContent(text));
                    break;
            }

            deferral.Complete();
        }
        private void OnProtocolActivatedAsync(ProtocolActivatedEventArgs protocol)
        {
            if (protocol.Uri.Scheme == "ms-ipmessaging")
            {
                var queryString = HttpUtility.ParseQueryString(protocol.Uri.Query);
                var ids = queryString.GetValues("ContactRemoteIds").FirstOrDefault();
                if (ids != null)
                {
                    var id = ulong.Parse(ids.Split(',')[0].Split('_')[1]);
                    if (!(Window.Current.Content is Frame rootFrame))
                    {
                        rootFrame = new Frame();
                        Window.Current.Content = rootFrame;
                    }

                    rootFrame.Navigate(typeof(MainPage), new MainPageArgs() { UserId = id, IsUriActivation = true });
                    Window.Current.Activate();
                    return;
                }
            }

            if (protocol.Uri.AbsolutePath.Trim('/').StartsWith("channels"))
            {
                var path = protocol.Uri.AbsolutePath.Split('/').Skip(1).ToArray();
                if (path.Length > 1 && ulong.TryParse(path[2], out var channel))
                {
                    if (!(Window.Current.Content is Frame rootFrame))
                    {
                        rootFrame = new Frame();
                        Window.Current.Content = rootFrame;
                    }

                    rootFrame.Navigate(typeof(MainPage), new MainPageArgs() { ChannelId = channel, IsUriActivation = true });
                    Window.Current.Activate();
                    return;
                }
            }

            OnLaunched(false, "");
        }

        private static async Task OnContactPanelActivated(ContactPanelActivatedEventArgs task)
        {

            if (!(Window.Current.Content is Frame rootFrame))
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();

                try
                {
                    var id = await ContactListManager.TryGetChannelIdAsync(task.Contact);
                    if (id != 0)
                    {
                        rootFrame.Navigate(typeof(MainPage), new MainPageArgs() { UserId = id, FullFrame = true, IsUriActivation = false });
                    }
                }
                catch
                {
                    var dialog = new MessageDialog("Something went wrong trying to find this person, sorry!", "Whoops!");
                    await dialog.ShowAsync();
                }

                // Place the frame in the current Window
                Window.Current.Content = rootFrame;
            }

            Window.Current.Activate();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            OnLaunched(e.PrelaunchActivated, e.Arguments, e.PreviousExecutionState);
        }

        private void OnLaunched(bool preLaunch, string arguments, ApplicationExecutionState previousState = ApplicationExecutionState.NotRunning)
        {
            Services.PerfTimings.Mark(preLaunch ? "OnLaunched (предзапуск)" : "OnLaunched");
            var channelId = 0ul;
            Exception themeLoadException = null;
            var args = ParseArgs(arguments);

            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active
            if (!(Window.Current.Content is Frame rootFrame))
            {

                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;

                if (previousState == ApplicationExecutionState.Terminated)
                    channelId = LocalSettings.Read("LastViewedChannel", 0ul);

                WindowingService.Current.SetMainWindow(rootFrame);
                Window.Current.Content = rootFrame;
            }

            if (args.TryGetValue("channelId", out var id) && ulong.TryParse(id, out var pId))
            {
                channelId = pId;
            }

            if (rootFrame.Content == null || channelId != 0)
                rootFrame.Navigate(typeof(MainPage), new MainPageArgs() { ChannelId = channelId, ThemeLoadException = themeLoadException });

            // Ensure the current window is active
            Window.Current.Activate();
        }

        private static Dictionary<string, string> ParseArgs(string arguments)
        {
            var rawArgs = Strings.SplitCommandLine(arguments);
            var args = new Dictionary<string, string>();
            foreach (var str in rawArgs)
            {
                if (!string.IsNullOrWhiteSpace(str) && str.Contains('-', '='))
                {
                    var arg = str.TrimStart('-');
                    args.Add(arg.Substring(0, arg.IndexOf('=')), arg.Substring(arg.IndexOf('=') + 1));
                }
            }

            return args;
        }

        protected override void OnShareTargetActivated(ShareTargetActivatedEventArgs args)
        {
            if (Window.Current.Content is not Frame rootFrame)
            {

                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();

                rootFrame.Navigate(typeof(MainPage), args.ShareOperation);

                // Place the frame in the current Window
                Window.Current.Content = rootFrame;
            }

            Window.Current.Activate();
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private DiscordClient temporaryCache;

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            if (Models.Voice.VoiceConnectionModel.HasActiveInAppCall)
            {
                Services.VoiceDiagnostics.Log("Приложение перешло в фон во время звонка (экран заблокирован или свёрнуто)");
                Services.VoiceDiagnostics.Flush();
            }
        }

        private void OnLeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            if (Models.Voice.VoiceConnectionModel.HasActiveInAppCall)
            {
                Services.VoiceDiagnostics.Log("Приложение вернулось на экран во время звонка");
                Services.VoiceDiagnostics.Flush();
            }
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();

            var inVoiceCall = Models.Voice.VoiceConnectionModel.HasActiveInAppCall;
            if (inVoiceCall)
            {
                Services.VoiceDiagnostics.Log("Suspending во время активного звонка — шлюз Discord не разрываю");
                Services.VoiceDiagnostics.Flush();
            }
            else if (DiscordManager.Discord != null)
            {
                temporaryCache = DiscordManager.Discord;
                await DiscordManager.Discord.DisconnectAsync(4002);
            }

            await Logger.OnSuspendingAsync();
            deferral.Complete();
        }

        private async void OnResuming(object sender, object e)
        {
            if (Models.Voice.VoiceConnectionModel.HasActiveInAppCall)
            {
                Services.VoiceDiagnostics.Log("Resuming во время активного звонка");
                Services.VoiceDiagnostics.Flush();
            }

            var cached = temporaryCache;
            temporaryCache = null;
            if (cached != null)
            {
                await cached.ConnectAsync();
            }
        }

        internal static async Task LogoutAsync()
        {
            await WebView.ClearTemporaryWebDataAsync();
            await WindowingService.Current.CloseAllWindowsAsync();
            await ImageCache.Instance.ClearAsync();
            await DiscordManager.LogoutAsync();

            try
            {
                var passwordVault = new PasswordVault();
                foreach (var c in passwordVault.FindAllByResource(TOKEN_IDENTIFIER))
                {
                    passwordVault.Remove(c);
                }
            }
            catch { }

            // the background process's copy (encrypted, and Unicord's old plain-text one)
            TokenStore.Clear();

            DiscordNavigationService.Reset();
            FullscreenService.Reset();
            OverlayService.Reset();
            SettingsService.Reset();
            SwipeOpenService.Reset();

            var frame = (Window.Current.Content as Frame);
            frame.Navigate(typeof(Page));
            frame.BackStack.Clear();
            frame.ForwardStack.Clear();

            frame = new Frame();
            frame.Navigate(typeof(MainPage));
            Window.Current.Content = frame;
        }

        internal static async Task LoginError(Exception ex)
        {
            if (ex != null)
            {
                var loader = ResourceLoader.GetForViewIndependentUse();
                // the gateway's 4004, or 401 from the API (a token that is not even a valid one)
                var rejected = ex is TokenRejectedException
                    || ex is DSharpPlus.Exceptions.UnauthorizedException
                    || ex.InnerException is DSharpPlus.Exceptions.UnauthorizedException;
                var message = rejected
                    ? loader.GetString("LoginTokenRejectedMessage")
                    : loader.GetString("LoginFailedDialogMessage");
                if (string.IsNullOrEmpty(message))
                    message = loader.GetString("LoginFailedDialogMessage");
                await UIUtilities.ShowErrorDialogAsync(loader.GetString("LoginFailedDialogTitle"), message);
                RoamingSettings.Save(VERIFY_LOGIN, false);
            }

            await DiscordManager.LogoutAsync();

            var mainPage = Window.Current.Content.FindChild<MainPage>();

            if (mainPage.FindChild<LoginPage>() == null)
            {
                mainPage.Nagivate(typeof(LoginPage));
            }

            mainPage.HideConnectingOverlay();
        }
    }
}
