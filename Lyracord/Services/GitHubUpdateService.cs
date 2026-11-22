using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;

namespace Unicord.Universal.Services
{
    internal sealed class GitHubReleaseAssetInfo
    {
        public string Name { get; set; }
        public string DownloadUrl { get; set; }
        public long SizeBytes { get; set; }
    }

    internal sealed class GitHubReleaseInfo
    {
        public string TagName { get; set; }
        public string Title { get; set; }
        public string DisplayVersion { get; set; }
        public Version ParsedVersion { get; set; }
        public string Body { get; set; }
        public string HtmlUrl { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public bool IsPrerelease { get; set; }
        public List<GitHubReleaseAssetInfo> Assets { get; } = new List<GitHubReleaseAssetInfo>();
        public GitHubReleaseAssetInfo PreferredAsset { get; set; }
    }

    internal enum GitHubUpdateStatus
    {
        Idle,
        Checking,
        UpToDate,
        UpdateAvailable,
        NoReleasesYet,
        RepoNotPublishedYet,
        Error
    }

    /// <summary>
    /// Checks GitHub Releases for new Lyracord versions, synchronizes release notes/versions from GitHub,
    /// and downloads architecture-matched (.appx) packages into the device's Downloads folder.
    /// </summary>
    internal static class GitHubUpdateService
    {
        public const string DefaultRepository = "lambdawesta/Lyracord";
        public const string RepoSettingKey = "GitHubUpdateRepository";

        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly SemaphoreSlim CheckLock = new SemaphoreSlim(1, 1);
        private static DateTimeOffset _lastCheckedUtc = DateTimeOffset.MinValue;
        private static bool _backgroundCheckStarted;

        public static GitHubUpdateStatus Status { get; private set; } = GitHubUpdateStatus.Idle;
        public static string LastError { get; private set; }
        public static GitHubReleaseInfo LatestRelease { get; private set; }
        public static IReadOnlyList<GitHubReleaseInfo> RecentReleases { get; private set; } = Array.Empty<GitHubReleaseInfo>();

        public static event EventHandler StateChanged;

        public static string Repository
        {
            get
            {
                var saved = App.LocalSettings.Read(RepoSettingKey, DefaultRepository)?.Trim();
                return string.IsNullOrWhiteSpace(saved) ? DefaultRepository : saved;
            }
            set
            {
                var cleaned = CleanRepositorySlug(value);
                App.LocalSettings.Save(RepoSettingKey, string.IsNullOrWhiteSpace(cleaned) ? DefaultRepository : cleaned);
            }
        }

        public static string RepositoryUrl => $"https://github.com/{Repository}";

        public static Version CurrentVersion
        {
            get
            {
                try
                {
                    var v = Package.Current.Id.Version;
                    return new Version(v.Major, v.Minor, v.Build, v.Revision);
                }
                catch (Exception)
                {
                    return new Version(1, 18, 13, 0);
                }
            }
        }

        public static string CurrentDisplayVersion
        {
            get
            {
                var v = CurrentVersion;
                return v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public static string CurrentArchitecture
        {
            get
            {
                try
                {
                    return Package.Current.Id.Architecture.ToString().ToUpperInvariant();
                }
                catch (Exception)
                {
                    return SystemPlatform.Mobile ? "ARM" : "X64";
                }
            }
        }

        public static bool IsUpdateAvailable
            => Status == GitHubUpdateStatus.UpdateAvailable && LatestRelease != null;

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lyracord", "1.18"));
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static void EnsureBackgroundCheckStarted()
        {
            if (_backgroundCheckStarted)
                return;
            _backgroundCheckStarted = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3500).ConfigureAwait(false);
                    await CheckForUpdatesAsync(forceRefresh: false).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            });
        }

        public static async Task<GitHubUpdateStatus> CheckForUpdatesAsync(bool forceRefresh = false)
        {
            if (!forceRefresh &&
                Status != GitHubUpdateStatus.Idle &&
                Status != GitHubUpdateStatus.Error &&
                (DateTimeOffset.UtcNow - _lastCheckedUtc) < TimeSpan.FromMinutes(10))
            {
                return Status;
            }

            await CheckLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Status = GitHubUpdateStatus.Checking;
                LastError = null;
                RaiseStateChanged();

                var repo = Repository;
                var url = $"https://api.github.com/repos/{repo}/releases?per_page=10";
                using (var response = await Http.GetAsync(url).ConfigureAwait(false))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        LatestRelease = null;
                        RecentReleases = Array.Empty<GitHubReleaseInfo>();
                        Status = GitHubUpdateStatus.RepoNotPublishedYet;
                        _lastCheckedUtc = DateTimeOffset.UtcNow;
                        VoiceDiagnostics.Log($"GitHub обновления: репозиторий {repo} ещё не опубликован (404)");
                        RaiseStateChanged();
                        return Status;
                    }

                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var array = JArray.Parse(json);

                    var parsedList = new List<GitHubReleaseInfo>();
                    foreach (var item in array.OfType<JObject>())
                    {
                        if (item["draft"]?.Value<bool>() == true)
                            continue;

                        var rel = ParseRelease(item);
                        if (rel != null)
                            parsedList.Add(rel);
                    }

                    RecentReleases = parsedList;
                    LatestRelease = parsedList
                        .OrderByDescending(r => r.ParsedVersion)
                        .FirstOrDefault();

                    _lastCheckedUtc = DateTimeOffset.UtcNow;

                    if (LatestRelease == null)
                    {
                        Status = GitHubUpdateStatus.NoReleasesYet;
                        VoiceDiagnostics.Log($"GitHub обновления ({repo}): релизов пока нет");
                    }
                    else if (CompareVersions(LatestRelease.ParsedVersion, CurrentVersion) > 0)
                    {
                        Status = GitHubUpdateStatus.UpdateAvailable;
                        VoiceDiagnostics.Log($"GitHub обновления ({repo}): доступна новая версия {LatestRelease.DisplayVersion} (текущая {CurrentDisplayVersion})");
                    }
                    else
                    {
                        Status = GitHubUpdateStatus.UpToDate;
                        VoiceDiagnostics.Log($"GitHub обновления ({repo}): актуальная версия ({CurrentDisplayVersion})");
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Status = GitHubUpdateStatus.Error;
                VoiceDiagnostics.Log("GitHub обновления: ошибка проверки", ex);
            }
            finally
            {
                CheckLock.Release();
                RaiseStateChanged();
            }

            return Status;
        }

        public static async Task<StorageFile> DownloadAndLaunchUpdateAsync(
            GitHubReleaseAssetInfo asset,
            Action<int, double, double> onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
                throw new ArgumentNullException(nameof(asset));

            var fileName = string.IsNullOrWhiteSpace(asset.Name) ? "Lyracord_Update.appx" : Path.GetFileName(asset.Name);
            VoiceDiagnostics.Log($"GitHub обновления: скачивание {fileName}…");

            StorageFile targetFile = null;
            try
            {
                targetFile = await DownloadsFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
            }
            catch (Exception)
            {
                targetFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl))
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? asset.SizeBytes;
                var totalMb = totalBytes > 0 ? totalBytes / (1024.0 * 1024.0) : 0;

                using (var networkStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var fileStream = await targetFile.OpenStreamForWriteAsync().ConfigureAwait(false))
                {
                    var buffer = new byte[65536];
                    long downloaded = 0;
                    int read;
                    var lastReport = Environment.TickCount;

                    while ((read = await networkStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        downloaded += read;

                        var now = Environment.TickCount;
                        if (onProgress != null && (now - lastReport >= 120 || downloaded == totalBytes))
                        {
                            lastReport = now;
                            var doneMb = downloaded / (1024.0 * 1024.0);
                            var pct = totalBytes > 0 ? (int)Math.Min(100, (downloaded * 100L) / totalBytes) : 0;
                            onProgress(pct, doneMb, totalMb);
                        }
                    }

                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            VoiceDiagnostics.Log($"GitHub обновления: файл сохранён ({targetFile.Name}), запуск установщика…");
            try
            {
                await Launcher.LaunchFileAsync(targetFile);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("GitHub обновления: автозапуск файла", ex);
            }

            return targetFile;
        }

        private static GitHubReleaseInfo ParseRelease(JObject obj)
        {
            var tag = obj["tag_name"]?.Value<string>()?.Trim();
            var name = obj["name"]?.Value<string>()?.Trim();
            if (!TryParseVersion(tag, out var parsed, out var display) &&
                !TryParseVersion(name, out parsed, out display))
            {
                return null;
            }

            var rel = new GitHubReleaseInfo
            {
                TagName = tag ?? display,
                Title = !string.IsNullOrWhiteSpace(name) ? name : (tag ?? display),
                DisplayVersion = display,
                ParsedVersion = parsed,
                Body = obj["body"]?.Value<string>()?.Trim(),
                HtmlUrl = obj["html_url"]?.Value<string>(),
                IsPrerelease = obj["prerelease"]?.Value<bool>() ?? false
            };

            var publishedRaw = obj["published_at"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(publishedRaw) && DateTimeOffset.TryParse(publishedRaw, out var dto))
                rel.PublishedAt = dto;

            if (obj["assets"] is JArray assetsArr)
            {
                foreach (var a in assetsArr.OfType<JObject>())
                {
                    var assetName = a["name"]?.Value<string>();
                    var downloadUrl = a["browser_download_url"]?.Value<string>();
                    var size = a["size"]?.Value<long>() ?? 0;
                    if (!string.IsNullOrWhiteSpace(assetName) && !string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        rel.Assets.Add(new GitHubReleaseAssetInfo
                        {
                            Name = assetName,
                            DownloadUrl = downloadUrl,
                            SizeBytes = size
                        });
                    }
                }
            }

            rel.PreferredAsset = PickPreferredAsset(rel.Assets, CurrentArchitecture);
            return rel;
        }

        private static GitHubReleaseAssetInfo PickPreferredAsset(List<GitHubReleaseAssetInfo> assets, string arch)
        {
            if (assets == null || assets.Count == 0)
                return null;

            var packages = assets
                .Where(a => a.Name.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ||
                            a.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ||
                            a.Name.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (packages.Count == 0)
                return null;

            var archToken = string.IsNullOrWhiteSpace(arch) ? "ARM" : arch;
            var exactArch = packages.FirstOrDefault(a =>
                a.Name.IndexOf($"_{archToken}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                a.Name.IndexOf($".{archToken}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                a.Name.IndexOf($"-{archToken}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                a.Name.IndexOf(archToken, StringComparison.OrdinalIgnoreCase) >= 0);

            return exactArch ?? packages[0];
        }

        public static bool TryParseVersion(string input, out Version version, out string displayVersion)
        {
            version = null;
            displayVersion = null;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            // Find first X.Y(.Z)(.W) numeric version sequence inside the tag or release title
            var s = input.Trim();
            var start = -1;
            var end = -1;
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsDigit(s[i]))
                {
                    if (start < 0)
                        start = i;
                    end = i;
                }
                else if (s[i] == '.' && start >= 0)
                {
                    end = i;
                }
                else if (start >= 0)
                {
                    if (end > start && s.Substring(start, end - start + 1).Contains("."))
                        break;
                    start = -1;
                    end = -1;
                }
            }

            if (start < 0 || end <= start)
                return false;

            var candidate = s.Substring(start, end - start + 1).Trim('.');
            var parts = candidate.Split('.');
            if (parts.Length < 2 || parts.Length > 4)
                return false;

            var nums = new int[4];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0)
                    return false;
            }

            version = new Version(nums[0], nums[1], parts.Length >= 3 ? nums[2] : 0, parts.Length == 4 ? nums[3] : 0);
            displayVersion = version.Revision > 0
                ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
                : $"{version.Major}.{version.Minor}.{version.Build}";
            return true;
        }

        public static int CompareVersions(Version a, Version b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            var na = new Version(Math.Max(0, a.Major), Math.Max(0, a.Minor), Math.Max(0, a.Build), Math.Max(0, a.Revision));
            var nb = new Version(Math.Max(0, b.Major), Math.Max(0, b.Minor), Math.Max(0, b.Build), Math.Max(0, b.Revision));
            return na.CompareTo(nb);
        }

        private static string CleanRepositorySlug(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return DefaultRepository;

            var s = raw.Trim();
            const string prefix = "https://github.com/";
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(prefix.Length);
            s = s.Trim('/');
            var parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : DefaultRepository;
        }

        private static void RaiseStateChanged()
        {
            try
            {
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }
    }
}
