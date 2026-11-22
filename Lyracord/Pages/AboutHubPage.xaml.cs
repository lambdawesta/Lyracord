using System;
using System.Collections.Generic;
using System.Linq;
using Unicord.Universal.Services;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// Metro "о версии" page opened from the hub tile and the Metro settings list: version, GitHub update check &
    /// version synchronization, the latest changes and the acknowledgements (licences), built from code with built-in resources only.
    /// </summary>
    public sealed partial class AboutHubPage : Page
    {
        // the changelog is the resource "About_Changes": lines "# version" start a version, the others are its items
        private static readonly (string Name, string WhatKey, string Licence, string Url)[] Credits =
        {
            ("Unicord", "Credit_Unicord", "MIT", "https://github.com/UnicordDev/Unicord/blob/master/LICENSE"),
            ("libdave", "Credit_Libdave", "MIT", "https://github.com/discord/libdave/blob/main/LICENSE"),
            ("MLSpp", "Credit_Mlspp", "BSD 2-Clause", "https://github.com/cisco/mlspp/blob/main/LICENSE"),
            ("OpenSSL", "Credit_OpenSSL", "Apache 2.0", "https://www.openssl.org/source/apache-license-2.0.txt"),
            ("WebRTC", "Credit_WebRTC", "BSD 3-Clause", "https://webrtc.googlesource.com/src/+/main/LICENSE"),
            ("DSharpPlus", "Credit_DSharpPlus", "MIT", "https://github.com/DSharpPlus/DSharpPlus/blob/master/LICENSE"),
            ("Opus, libsodium", "Credit_OpusSodium", "BSD / ISC", "https://opus-codec.org/license/"),
            ("Windows Community Toolkit", "Credit_Toolkit", "MIT", "https://github.com/CommunityToolkit/WindowsCommunityToolkit/blob/main/License.md"),
            ("Newtonsoft.Json", "Credit_Newtonsoft", "MIT", "https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md"),
        };

        private Border _updateCard;
        private TextBlock _updateTitleText;
        private TextBlock _updateSubText;
        private TextBlock _updateNotesText;
        private ProgressBar _downloadProgress;
        private TextBlock _downloadStatusText;
        private Button _downloadButton;
        private TextBlock _downloadButtonLabel;
        private Button _checkButton;
        private Button _githubButton;
        private StackPanel _changelogPanel;
        private bool _isDownloading;

        public AboutHubPage()
        {
            InitializeComponent();

            var version = GitHubUpdateService.CurrentDisplayVersion;
            var arch = GitHubUpdateService.CurrentArchitecture;

            Add(new TextBlock
            {
                Text = $"Lyracord {version}",
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiLight,
                FontSize = 30,
                Foreground = Fore()
            });
            Add(new TextBlock
            {
                Text = Loc.T("About_Description"),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
                Margin = new Thickness(0, 2, 0, 0)
            });
            Add(new TextBlock
            {
                Text = $"{Loc.F("About_Developer", "lambdawesta")} · {arch}",
                FontSize = 15,
                Foreground = Fore(),
                Margin = new Thickness(0, 6, 0, 0)
            });

            BuildUpdatesSection();

            Header(Loc.T("About_WhatsNew"));
            _changelogPanel = new StackPanel();
            Add(_changelogPanel);
            RebuildChangelog();

            Header(Loc.T("About_Thanks"));
            foreach (var (name, whatKey, licence, url) in Credits)
            {
                Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiLight, Foreground = Fore(), Margin = new Thickness(0, 8, 0, 0) });
                Add(new TextBlock { Text = Loc.T(whatKey), FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Secondary() });

                var link = new TextBlock { FontSize = 14 };
                var hyperlink = new Hyperlink();
                try { hyperlink.NavigateUri = new Uri(url); } catch (Exception) { }
                hyperlink.Inlines.Add(new Run { Text = Loc.F("About_License", licence) });
                link.Inlines.Add(hyperlink);
                Add(link);
            }
        }

        private void BuildUpdatesSection()
        {
            Header(Loc.T("About_UpdatesHeader"));

            var cardPanel = new StackPanel();

            _updateTitleText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiLight,
                FontSize = 21,
                Foreground = Fore(),
                TextWrapping = TextWrapping.Wrap
            };
            cardPanel.Children.Add(_updateTitleText);

            _updateSubText = new TextBlock
            {
                FontSize = 14,
                Foreground = Secondary(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            cardPanel.Children.Add(_updateSubText);

            _updateNotesText = new TextBlock
            {
                FontSize = 14,
                Foreground = Fore(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            cardPanel.Children.Add(_updateNotesText);

            _downloadProgress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 6,
                Margin = new Thickness(0, 10, 0, 4),
                Visibility = Visibility.Collapsed
            };
            cardPanel.Children.Add(_downloadProgress);

            _downloadStatusText = new TextBlock
            {
                FontSize = 13,
                Foreground = Secondary(),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            cardPanel.Children.Add(_downloadStatusText);

            var actions = new Microsoft.Toolkit.Uwp.UI.Controls.WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalSpacing = 10,
                VerticalSpacing = 10,
                Margin = new Thickness(0, 12, 0, 0)
            };

            _downloadButton = CreateMetroButton("\uE896", Loc.T("Update_DownloadButton"), out _downloadButtonLabel);
            _downloadButton.Visibility = Visibility.Collapsed;
            _downloadButton.Click += DownloadButton_Click;
            actions.Children.Add(_downloadButton);

            _checkButton = CreateMetroButton("\uE72C", Loc.T("Update_CheckButton"), out _);
            _checkButton.Click += CheckButton_Click;
            actions.Children.Add(_checkButton);

            _githubButton = CreateMetroButton("\uE8A7", "github", out _);
            _githubButton.Click += GitHubButton_Click;
            actions.Children.Add(_githubButton);

            cardPanel.Children.Add(actions);

            _updateCard = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = BorderBrushColor(),
                Background = CardBg(),
                Padding = new Thickness(14, 12, 14, 14),
                Margin = new Thickness(0, 4, 0, 0),
                Child = cardPanel
            };

            Add(_updateCard);
            RefreshUpdateUi();
        }

        private Button CreateMetroButton(string glyph, string label, out TextBlock textBlock)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = glyph,
                FontSize = 14,
                Margin = new Thickness(0, 1, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            textBlock = new TextBlock
            {
                Text = label,
                FontSize = 15,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(textBlock);

            return new Button
            {
                Content = panel,
                Background = new SolidColorBrush(Colors.Transparent),
                Foreground = Fore(),
                BorderBrush = Fore(),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(12, 5, 12, 6)
            };
        }

        private void RefreshUpdateUi()
        {
            var current = GitHubUpdateService.CurrentDisplayVersion;
            var arch = GitHubUpdateService.CurrentArchitecture;
            var repo = GitHubUpdateService.Repository;
            var latest = GitHubUpdateService.LatestRelease;
            var status = GitHubUpdateService.Status;

            _checkButton.IsEnabled = status != GitHubUpdateStatus.Checking && !_isDownloading;
            _updateNotesText.Visibility = Visibility.Collapsed;

            switch (status)
            {
                case GitHubUpdateStatus.Checking:
                    _updateCard.BorderBrush = BorderBrushColor();
                    _updateTitleText.Text = Loc.T("Update_StatusChecking");
                    _updateSubText.Text = $"github.com/{repo} · Lyracord {current} ({arch})";
                    _downloadButton.Visibility = Visibility.Collapsed;
                    break;

                case GitHubUpdateStatus.UpdateAvailable when latest != null:
                    _updateCard.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x23, 0xA5, 0x5A));
                    _updateTitleText.Text = Loc.F("Update_StatusAvailable", latest.DisplayVersion);
                    if (latest.PreferredAsset != null)
                    {
                        var mb = (latest.PreferredAsset.SizeBytes / (1024.0 * 1024.0)).ToString("0.0");
                        _updateSubText.Text = $"{latest.PreferredAsset.Name} ({mb} MB) · github.com/{repo}";
                        _downloadButtonLabel.Text = Loc.F("Update_DownloadVersion", latest.DisplayVersion);
                        _downloadButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _updateSubText.Text = $"github.com/{repo} · {Loc.F("Update_InstalledVersion", current, arch)}";
                        _downloadButton.Visibility = Visibility.Collapsed;
                    }

                    if (!string.IsNullOrWhiteSpace(latest.Body))
                    {
                        _updateNotesText.Text = FormatGitHubBody(latest.Body);
                        _updateNotesText.Visibility = Visibility.Visible;
                    }
                    break;

                case GitHubUpdateStatus.UpToDate:
                    _updateCard.BorderBrush = BorderBrushColor();
                    _updateTitleText.Text = Loc.F("Update_StatusUpToDate", current);
                    _updateSubText.Text = $"github.com/{repo} · {arch}";
                    if (latest?.PreferredAsset != null)
                    {
                        _downloadButtonLabel.Text = Loc.T("Update_DownloadButton");
                        _downloadButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _downloadButton.Visibility = Visibility.Collapsed;
                    }
                    break;

                case GitHubUpdateStatus.NoReleasesYet:
                case GitHubUpdateStatus.RepoNotPublishedYet:
                    _updateCard.BorderBrush = BorderBrushColor();
                    _updateTitleText.Text = Loc.F("Update_InstalledVersion", current, arch);
                    _updateSubText.Text = Loc.F("Update_StatusNoReleases", $"github.com/{repo}");
                    _downloadButton.Visibility = Visibility.Collapsed;
                    break;

                case GitHubUpdateStatus.Error:
                    _updateCard.BorderBrush = BorderBrushColor();
                    _updateTitleText.Text = Loc.F("Update_InstalledVersion", current, arch);
                    _updateSubText.Text = Loc.F("Update_StatusError", GitHubUpdateService.LastError ?? "GitHub API");
                    _downloadButton.Visibility = Visibility.Collapsed;
                    break;

                default:
                    _updateCard.BorderBrush = BorderBrushColor();
                    _updateTitleText.Text = Loc.F("Update_InstalledVersion", current, arch);
                    _updateSubText.Text = $"github.com/{repo}";
                    _downloadButton.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void RebuildChangelog()
        {
            if (_changelogPanel == null)
                return;

            _changelogPanel.Children.Clear();
            var builtInRaw = Loc.T("About_Changes");
            var knownVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in builtInRaw.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#"))
                {
                    var v = line.TrimStart('#').Trim();
                    if (GitHubUpdateService.TryParseVersion(v, out _, out var norm))
                        knownVersions.Add(norm);
                    knownVersions.Add(v);
                }
            }

            // First, prepend any releases from GitHub that are not yet in the local built-in changelog
            var remoteReleases = GitHubUpdateService.RecentReleases;
            if (remoteReleases != null && remoteReleases.Count > 0)
            {
                foreach (var rel in remoteReleases.OrderByDescending(r => r.ParsedVersion))
                {
                    if (knownVersions.Contains(rel.DisplayVersion))
                        continue;

                    _changelogPanel.Children.Add(new TextBlock
                    {
                        Text = $"{rel.DisplayVersion} (GitHub)",
                        FontSize = 20,
                        FontWeight = FontWeights.SemiLight,
                        Foreground = Fore(),
                        Margin = new Thickness(0, 8, 0, 2)
                    });

                    var notes = FormatGitHubBodyLines(rel.Body);
                    if (notes.Count == 0)
                    {
                        _changelogPanel.Children.Add(new TextBlock
                        {
                            Text = "· " + rel.Title,
                            FontSize = 15,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Secondary()
                        });
                    }
                    else
                    {
                        foreach (var item in notes)
                        {
                            _changelogPanel.Children.Add(new TextBlock
                            {
                                Text = "· " + item,
                                FontSize = 15,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Secondary()
                            });
                        }
                    }
                }
            }

            foreach (var raw in builtInRaw.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith("#"))
                {
                    _changelogPanel.Children.Add(new TextBlock
                    {
                        Text = line.TrimStart('#').Trim(),
                        FontSize = 20,
                        FontWeight = FontWeights.SemiLight,
                        Foreground = Fore(),
                        Margin = new Thickness(0, 8, 0, 2)
                    });
                }
                else
                {
                    _changelogPanel.Children.Add(new TextBlock
                    {
                        Text = "· " + line,
                        FontSize = 15,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Secondary()
                    });
                }
            }
        }

        private static List<string> FormatGitHubBodyLines(string body)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(body))
                return result;

            foreach (var raw in body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                line = line.TrimStart('-', '*', '•', '·', ' ');
                if (line.Length > 0)
                    result.Add(line);
            }
            return result;
        }

        private static string FormatGitHubBody(string body)
        {
            var lines = FormatGitHubBodyLines(body);
            if (lines.Count == 0)
                return body.Trim();
            return string.Join("\n", lines.Take(8).Select(l => "· " + l));
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await GitHubUpdateService.CheckForUpdatesAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("О версии: проверка обновлений", ex);
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var asset = GitHubUpdateService.LatestRelease?.PreferredAsset;
            if (asset == null || _isDownloading)
                return;

            _isDownloading = true;
            _downloadButton.IsEnabled = false;
            _checkButton.IsEnabled = false;
            _downloadProgress.Value = 0;
            _downloadProgress.Visibility = Visibility.Visible;
            _downloadStatusText.Text = Loc.T("Update_DownloadingStart");
            _downloadStatusText.Visibility = Visibility.Visible;

            try
            {
                var file = await GitHubUpdateService.DownloadAndLaunchUpdateAsync(asset, (pct, doneMb, totalMb) =>
                {
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        _downloadProgress.Value = pct;
                        _downloadStatusText.Text = totalMb > 0
                            ? Loc.F("Update_DownloadingProgress", pct, doneMb.ToString("0.0"), totalMb.ToString("0.0"))
                            : $"{pct}% ({doneMb:0.0} MB)";
                    });
                });

                _downloadProgress.Value = 100;
                _downloadStatusText.Text = Loc.F("Update_DownloadedSaved", file.Name);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("О версии: скачивание обновления", ex);
                _downloadStatusText.Text = Loc.F("Update_StatusError", ex.Message);
            }
            finally
            {
                _isDownloading = false;
                _downloadButton.IsEnabled = true;
                _checkButton.IsEnabled = true;
            }
        }

        private async void GitHubButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = GitHubUpdateService.LatestRelease?.HtmlUrl ?? GitHubUpdateService.RepositoryUrl;
                await Launcher.LaunchUriAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("О версии: открыть GitHub", ex);
            }
        }

        private void OnUpdateStateChanged(object sender, EventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                RefreshUpdateUi();
                RebuildChangelog();
            });
        }

        private void Header(string text)
            => Add(new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 60,
                Foreground = Secondary(),
                Margin = new Thickness(0, 28, 0, 4)
            });

        private void Add(UIElement element) => Items.Children.Add(element);

        private Brush Fore() => new SolidColorBrush(MetroColors.C(MetroUi.IsLight(this) ? 0x000000 : 0xFFFFFF));

        private Brush Secondary() => new SolidColorBrush(MetroColors.C(MetroUi.IsLight(this) ? 0x6B6B6B : 0x9A9A9A));

        private Brush CardBg() => new SolidColorBrush(MetroColors.C(MetroUi.IsLight(this) ? 0xF3F3F3 : 0x121212));

        private Brush BorderBrushColor() => new SolidColorBrush(MetroColors.C(MetroUi.IsLight(this) ? 0xCCCCCC : 0x333333));

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            MetroTransitions.PageIn(this, e.NavigationMode);
            MetroTransitions.FeatherAfterLayout(Items);

            GitHubUpdateService.StateChanged -= OnUpdateStateChanged;
            GitHubUpdateService.StateChanged += OnUpdateStateChanged;
            RefreshUpdateUi();
            RebuildChangelog();

            if (GitHubUpdateService.Status == GitHubUpdateStatus.Idle)
            {
                _ = GitHubUpdateService.CheckForUpdatesAsync(forceRefresh: false);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            GitHubUpdateService.StateChanged -= OnUpdateStateChanged;
        }
    }
}
