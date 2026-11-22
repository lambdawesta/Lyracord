using System;
using DSharpPlus.Entities;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using Windows.ApplicationModel;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Content Dialog item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Unicord.Universal.Dialogs
{
    public sealed partial class CaptchaRequestDialog : ContentDialog
    {
        private readonly DiscordCaptchaRequest captchaRequest;
        
        public DiscordCaptchaResponse CaptchaResponse;

        public CaptchaRequestDialog(DiscordCaptchaRequest captchaRequest)
        {
            this.InitializeComponent();
            Utilities.UIUtilities.KeepClearOfSystemBars(this);

            this.captchaRequest = captchaRequest;
            this.CaptchaResponse = new DiscordCaptchaResponse();
        }

        private async void ContentDialog_Loaded(object sender, RoutedEventArgs e)
        {
            var theme = Application.Current.RequestedTheme == ApplicationTheme.Dark ? "dark" : "light";

            try
            {
                await CaptchaWebView.EnsureCoreWebView2Async(); // ensure the CoreWebView2 is created
                var coreWebView = CaptchaWebView.CoreWebView2;
                if (coreWebView != null)
                {
                    coreWebView.SetVirtualHostNameToFolderMapping(
                        "appx", Package.Current.InstalledLocation.Path,
                        CoreWebView2HostResourceAccessKind.Allow);

                    coreWebView.WebMessageReceived += OnMessageRecieved;

                    CaptchaWebView.Source = new Uri($"https://appx/Assets/Captcha/hCaptcha.html?siteKey={captchaRequest.SiteKey}&theme=" + theme);
                    return;
                }
            }
            catch (Exception ex)
            {
                // WebView2 does not exist on Windows 10 Mobile; this used to be an unhandled error in an async void
                // handler, which closes the app
                try { Services.VoiceDiagnostics.Log("Капча: WebView2 недоступен", ex); } catch (Exception) { }
            }

            ShowUnsupported();
        }

        /// <summary>Captcha cannot be shown here: explain it instead of an empty dialog.</summary>
        internal void ShowUnsupported()
        {
            Content = new TextBlock
            {
                Text = Services.Loc.T("Captcha_Unsupported"),
                TextWrapping = TextWrapping.Wrap
            };
            IsPrimaryButtonEnabled = false;
            CloseButtonText = Services.Loc.T("Common_Close");
        }

        private async void OnMessageRecieved(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var jObject = JObject.Parse(args.WebMessageAsJson);
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var op = jObject["op"].ToObject<string>();
                switch (op)
                {
                    case "captcha_complete":
                        CaptchaResponse = new DiscordCaptchaResponse(jObject["token"].ToObject<string>());
                        IsPrimaryButtonEnabled = true;
                        break;
                };
            });
        }
    }
}
