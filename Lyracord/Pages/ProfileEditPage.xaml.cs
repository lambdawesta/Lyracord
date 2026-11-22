using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unicord.Universal.Models.User;
using Unicord.Universal.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// Edits the global profile through PATCH /users/@me/profile: bio, pronouns, banner colour and (Nitro only)
    /// the banner picture. Only changed fields are sent.
    /// </summary>
    public sealed partial class ProfileEditPage : Page
    {
        private const long MaxBannerBytes = 10 * 1024 * 1024;

        private string _origBio = "", _origPronouns = "";
        private Color? _origColor, _color;
        private bool _hadBanner;
        private string _newBanner;     // data URI of a picked picture
        private bool _removeBanner;
        private bool _loading;

        public ProfileEditPage()
        {
            InitializeComponent();
            BuildSwatches();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            MetroTransitions.PageIn(this, e.NavigationMode);
            var me = DiscordManager.Discord?.CurrentUser;
            if (me == null)
                return;

            LoadingText.Visibility = Visibility.Visible;
            Editor.Visibility = Visibility.Collapsed;
            try
            {
                UserProfileInfo.Invalidate(me.Id);
                var profile = await UserProfileInfo.LoadAsync(me.Id, null);

                _loading = true;
                _origBio = profile.Bio ?? "";
                _origPronouns = profile.Pronouns ?? "";
                _origColor = _color = profile.AccentColor;
                _hadBanner = profile.Banner != null;
                _newBanner = null;
                _removeBanner = false;

                BioBox.Text = _origBio;
                PronounsBox.Text = _origPronouns;
                ColorBox.Text = _color.HasValue ? MetroColors.ToHex(_color.Value) : "";
                BannerImage.Source = profile.Banner != null ? new BitmapImage(profile.Banner) : null;
                UpdateBannerColor();

                // banner pictures need Nitro Classic (1) or Nitro (2)
                var canBanner = profile.PremiumType == 1 || profile.PremiumType == 2;
                PickBannerButton.IsEnabled = canBanner;
                NitroNote.Visibility = canBanner ? Visibility.Collapsed : Visibility.Visible;
                RemoveBannerButton.IsEnabled = _hadBanner;
                _loading = false;

                LoadingText.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Редактор профиля: загрузка", ex);
                LoadingText.Text = Loc.F("Profile_LoadFailed", Describe(ex));
            }
        }

        // ------------------------------------------------------------------ banner colour

        private void BuildSwatches()
        {
            foreach (var (name, color) in MetroColors.Palette)
            {
                var swatch = new Border { Width = 40, Height = 40, Background = new SolidColorBrush(color) };
                ToolTipService.SetToolTip(swatch, Loc.T("Color_" + name));
                swatch.Tapped += (s, e) => ColorBox.Text = MetroColors.ToHex(color);
                ColorSwatches.Children.Add(swatch);
            }
        }

        private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (MetroColors.TryParseHex(ColorBox.Text, out var c))
                _color = c;
            else if (string.IsNullOrWhiteSpace(ColorBox.Text))
                _color = null;
            UpdateBannerColor();
        }

        private void NoColor_Click(object sender, RoutedEventArgs e) => ColorBox.Text = "";

        private void UpdateBannerColor()
        {
            BannerPreview.Background = _color.HasValue
                ? new SolidColorBrush(_color.Value)
                : new SolidColorBrush(MetroColors.C(MetroUi.IsLight(this) ? 0xE0E0E0 : 0x262626));
        }

        // ------------------------------------------------------------------ banner picture

        private async void PickBanner_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".gif");
                var file = await picker.PickSingleFileAsync();
                if (file == null)
                    return;

                var props = await file.GetBasicPropertiesAsync();
                if ((long)props.Size > MaxBannerBytes)
                {
                    StatusText.Text = Loc.T("Profile_TooBig");
                    return;
                }

                var buffer = await FileIO.ReadBufferAsync(file);
                var bytes = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer))
                    reader.ReadBytes(bytes);

                var ext = Path.GetExtension(file.Name).ToLowerInvariant();
                var mime = ext == ".png" ? "image/png" : ext == ".gif" ? "image/gif" : "image/jpeg";
                _newBanner = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                _removeBanner = false;

                var preview = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await preview.SetSourceAsync(stream);
                BannerImage.Source = preview;
                RemoveBannerButton.IsEnabled = true;
                StatusText.Text = Loc.T("Profile_Picked");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Редактор профиля: баннер", ex);
                StatusText.Text = Loc.F("Profile_OpenFailed", ex.Message);
            }
        }

        private void RemoveBanner_Click(object sender, RoutedEventArgs e)
        {
            _newBanner = null;
            _removeBanner = _hadBanner;
            BannerImage.Source = null;
            RemoveBannerButton.IsEnabled = false;
        }

        // ------------------------------------------------------------------ text fields

        private void BioBox_TextChanged(object sender, TextChangedEventArgs e)
            => BioCounter.Text = $"{BioBox.Text.Length} / 190";

        // ------------------------------------------------------------------ save

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            var body = new JObject();
            var bio = BioBox.Text.Replace("\r", "\n").Trim();
            if (bio != _origBio)
                body["bio"] = bio;
            var pronouns = PronounsBox.Text.Trim();
            if (pronouns != _origPronouns)
                body["pronouns"] = pronouns;
            if (_color != _origColor)
                body["accent_color"] = _color.HasValue ? (JToken)MetroColors.ToRgb(_color.Value) : JValue.CreateNull();
            if (_newBanner != null)
                body["banner"] = _newBanner;
            else if (_removeBanner)
                body["banner"] = JValue.CreateNull();

            if (body.Count == 0)
            {
                StatusText.Text = Loc.T("Profile_NoChanges");
                return;
            }

            SaveButton.IsEnabled = false;
            StatusText.Text = Loc.T("Profile_Saving");
            try
            {
                await DiscordManager.Discord.ModifyUserProfileRawAsync(body.ToString(Formatting.None));

                var me = DiscordManager.Discord.CurrentUser;
                UserProfileInfo.Invalidate(me.Id);
                _origBio = bio;
                _origPronouns = pronouns;
                _origColor = _color;
                _hadBanner = _newBanner != null || (_hadBanner && !_removeBanner);
                _newBanner = null;
                _removeBanner = false;
                StatusText.Text = Loc.T("Profile_Saved");
                VoiceDiagnostics.Log($"Редактор профиля: сохранено ({string.Join(", ", body.Properties().Select(p => p.Name))})");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Редактор профиля: сохранение", ex);
                StatusText.Text = Loc.F("Profile_Rejected", Describe(ex));
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }

        private static string Describe(Exception ex)
            => ex is DSharpPlus.Exceptions.DiscordException de && !string.IsNullOrEmpty(de.JsonMessage) ? de.JsonMessage : ex.Message;
    }
}
