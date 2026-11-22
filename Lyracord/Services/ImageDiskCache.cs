using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Unicord.Universal.Services
{
    /// <summary>
    /// Avatars, server icons and other small pictures kept on the phone (LocalState\imagecache), so lists show them
    /// at once instead of downloading them again every time. Discord picture URLs carry the image hash, so a changed
    /// avatar is a new URL and a new file; files unused for a month are removed.
    /// </summary>
    internal static class ImageDiskCache
    {
        private const string FolderName = "imagecache";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        private const long MaxCacheBytes = 50L * 1024 * 1024;
        private const long TargetCacheBytes = 40L * 1024 * 1024;
        private const int MaxCacheFiles = 1500;
        private const int TargetCacheFiles = 1200;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Task<bool>> _downloads = new Dictionary<string, Task<bool>>();
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static HashSet<string> _known;
        private static string _folder;

        /// <summary>The local copy of <paramref name="url"/> if it is cached; otherwise the URL itself, and a copy is fetched for next time.</summary>
        public static string Resolve(string url)
        {
            if (!TryName(url, out var name))
                return url;
            if (IsKnown(name))
                return LocalUri(name);
            _ = DownloadAsync(url, name);
            return url;
        }

        /// <summary>A picture for code-built lists: shown from the phone when cached, otherwise downloaded once and kept.</summary>
        public static BitmapImage Create(string url, int decodeWidth)
        {
            var image = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };
            Load(image, url);
            return image;
        }

        public static async void Load(BitmapImage image, string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return;
                if (!TryName(url, out var name))
                {
                    image.UriSource = uri;
                    return;
                }
                if (!IsKnown(name) && !await DownloadAsync(url, name))
                {
                    image.UriSource = uri; // no copy on the phone: let the image load it the normal way
                    return;
                }
                // if the copy on the phone cannot be shown, forget it and take the picture from the network
                ExceptionRoutedEventHandler failed = null;
                failed = (s, e) =>
                {
                    image.ImageFailed -= failed;
                    Forget(name);
                    try { image.UriSource = uri; } catch (Exception) { }
                };
                image.ImageFailed += failed;
                image.UriSource = new Uri(LocalUri(name));
            }
            catch (Exception)
            {
                // a missing picture must never break a page
            }
        }

        // ms-appdata only reaches local, roaming and temp, not LocalCache: 1.14.3 kept the files in LocalCache and
        // every picture failed to show. The cache now lives in LocalFolder\imagecache.
        private static string LocalUri(string name) => $"ms-appdata:///local/{FolderName}/{name}";

        private static void Forget(string name)
        {
            string path = null;
            lock (_lock)
            {
                _known?.Remove(name);
                if (_folder != null)
                    path = Path.Combine(_folder, name);
            }
            if (path != null)
                Task.Run(() => { try { File.Delete(path); } catch (Exception) { } });
        }

        private static bool TryName(string url, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            if (uri.Scheme != "https" && uri.Scheme != "http")
                return false;

            // FNV-1a 64 of the whole URL (size and format are part of it), keeping the picture's extension
            ulong hash = 14695981039346656037;
            foreach (var c in url)
            {
                hash ^= c;
                hash *= 1099511628211;
            }
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5)
                ext = ".png";
            name = hash.ToString("x16") + ext.ToLowerInvariant();
            return true;
        }

        private static bool IsKnown(string name)
        {
            lock (_lock)
            {
                EnsureFolder();
                return _known.Contains(name);
            }
        }

        // must be called under _lock
        private static void EnsureFolder()
        {
            if (_known != null)
                return;
            _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                _folder = Path.Combine(ApplicationData.Current.LocalFolder.Path, FolderName);
                Directory.CreateDirectory(_folder);

                // leftovers of 1.14.3/1.15.0, which used LocalCache (unreachable through ms-appdata)
                var oldFolder = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, FolderName);
                if (Directory.Exists(oldFolder))
                    Task.Run(() => { try { Directory.Delete(oldFolder, true); } catch (Exception) { } });
                var old = new List<string>();
                var kept = new List<(string Path, string Name, long Size, DateTime ModifiedUtc)>();
                long totalBytes = 0;
                var cutoff = DateTime.UtcNow - MaxAge;
                foreach (var file in Directory.GetFiles(_folder))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || fi.LastWriteTimeUtc < cutoff)
                        {
                            old.Add(file);
                        }
                        else
                        {
                            var name = fi.Name;
                            _known.Add(name);
                            totalBytes += fi.Length;
                            kept.Add((file, name, fi.Length, fi.LastWriteTimeUtc));
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                // Cap total disk usage and file count (oldest files first) so the cache cannot grow unbounded.
                if (totalBytes > MaxCacheBytes || kept.Count > MaxCacheFiles)
                {
                    kept.Sort((a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc));
                    var remainingFiles = kept.Count;
                    foreach (var entry in kept)
                    {
                        if (totalBytes <= TargetCacheBytes && remainingFiles <= TargetCacheFiles)
                            break;
                        old.Add(entry.Path);
                        _known.Remove(entry.Name);
                        totalBytes -= entry.Size;
                        remainingFiles--;
                    }
                }

                if (old.Count > 0)
                {
                    Task.Run(() =>
                    {
                        foreach (var file in old)
                        {
                            try { File.Delete(file); } catch (Exception) { }
                        }
                    });
                }
            }
            catch (Exception)
            {
                _folder = null;
            }
        }

        /// <summary>Returns the current cached file count and total size in bytes.</summary>
        public static Task<(int Files, long Bytes)> GetStatsAsync()
        {
            return Task.Run(() =>
            {
                string folder;
                lock (_lock)
                {
                    EnsureFolder();
                    folder = _folder;
                }
                if (folder == null || !Directory.Exists(folder))
                    return (0, 0L);

                var count = 0;
                long bytes = 0;
                foreach (var file in Directory.GetFiles(folder))
                {
                    try
                    {
                        if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                            continue;
                        count++;
                        bytes += new FileInfo(file).Length;
                    }
                    catch (Exception)
                    {
                    }
                }
                return (count, bytes);
            });
        }

        /// <summary>Deletes all cached pictures from LocalState\imagecache.</summary>
        public static Task ClearAsync()
        {
            return Task.Run(() =>
            {
                string folder;
                lock (_lock)
                {
                    EnsureFolder();
                    folder = _folder;
                    _known?.Clear();
                }
                if (folder == null || !Directory.Exists(folder))
                    return;

                foreach (var file in Directory.GetFiles(folder))
                {
                    try { File.Delete(file); } catch (Exception) { }
                }
            });
        }

        private static Task<bool> DownloadAsync(string url, string name)
        {
            lock (_lock)
            {
                EnsureFolder();
                if (_folder == null)
                    return Task.FromResult(false);
                if (_downloads.TryGetValue(name, out var running))
                    return running;
                var task = Task.Run(() => FetchAsync(url, name));
                _downloads[name] = task;
                return task;
            }
        }

        private static async Task<bool> FetchAsync(string url, string name)
        {
            var path = Path.Combine(_folder, name);
            var temp = path + ".part";
            try
            {
                var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0)
                    return false;
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
                lock (_lock)
                    _known.Add(name);
                return true;
            }
            catch (Exception)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                return false;
            }
            finally
            {
                lock (_lock)
                    _downloads.Remove(name);
            }
        }
    }
}
