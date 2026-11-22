using System.Threading.Tasks;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models;
using Unicord.Universal.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Unicord.Universal.Pages.Settings
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class VoiceSettingsPage : Page
    {
        private VoiceSettingsModel Model { get; set; }

        public VoiceSettingsPage()
        {
            Model = new VoiceSettingsModel();
            Model.PropertyChanged += Model_PropertyChanged;

            InitializeComponent();
        }

        private async void Model_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            await UpdateVoiceSettings();
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await Model.LoadAsync();
            DataContext = Model;
            Model.InvokePropertyChanged("");
            await RefreshReportAsync();
        }

        // Results of the microphone tests run in this session, kept for the copied report.
        private static readonly System.Collections.Generic.List<string> _testResults = new System.Collections.Generic.List<string>();

        private async Task RefreshReportAsync()
        {
            DiagReport.Text = await Services.VoiceDiagnostics.BuildReportAsync(_testResults.Count == 0 ? null : string.Join("\n", _testResults));
        }

        private async Task RunTestAsync(bool callMode)
        {
            MicTestMediaButton.IsEnabled = false;
            MicTestCallButton.IsEnabled = false;
            DiagStatus.Text = Loc.T("VoiceDiag_Recording");
            try
            {
                var result = await Services.VoiceDiagnostics.LoopbackTestAsync(callMode);
                _testResults.Add(result.TrimEnd());
                DiagStatus.Text = Loc.T("VoiceDiag_Done");
            }
            finally
            {
                MicTestMediaButton.IsEnabled = true;
                MicTestCallButton.IsEnabled = true;
            }
            await RefreshReportAsync();
        }

        private async void MicTestMedia_Click(object sender, RoutedEventArgs e) => await RunTestAsync(false);

        private async void MicTestCall_Click(object sender, RoutedEventArgs e) => await RunTestAsync(true);

        private async void RefreshReport_Click(object sender, RoutedEventArgs e) => await RefreshReportAsync();

        private async void CopyReport_Click(object sender, RoutedEventArgs e)
        {
            await RefreshReportAsync();
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(DiagReport.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            DiagStatus.Text = Loc.T("VoiceDiag_Copied");
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            await Model.SaveAsync();
            await UpdateVoiceSettings();
        }

        private async Task UpdateVoiceSettings()
        {
            try
            {
                if (this.FindParent<DiscordPage>()?.DataContext is DiscordPageViewModel model)
                {
                    var inputInfo = Model.InputDevice;
                    var outputInfo = Model.OutputDevice;
                    //await (model?.VoiceModel?.UpdatePreferredAudioDevicesAsync(outputInfo?.Id, inputInfo?.Id) ?? Task.CompletedTask);
                }
            }
            catch { }
        }
    }
}
