using BeatSaberPlaylistsLib.Types;
using BeatSaverSharp;
using BeatSaverSharp.Models;
using IPA.Loader;
using PlaylistManager.Configuration;
using PlaylistManager.Types;
using SiraUtil.Zenject;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlaylistManager.Utilities;
using SongCore;
using Zenject;
using SongPackageIO;
using UnityEngine.SceneManagement;
using UnityEngine;
using HMUI;

namespace PlaylistManager.Downloaders
{
    internal class PlaylistSequentialDownloader : IInitializable, IDisposable
    {
        private readonly BeatSaver beatSaverInstance;
        private readonly CancellationTokenSource lifetime = new();
        private Task queueTask = Task.CompletedTask;
        private bool draining;
        private static readonly HashSet<string> ownedHashes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object ownedHashesGate = new();
        private DownloadQueueEntry currentDownload;

        private readonly PlaylistHttpService archiveHttp;
        private bool refreshRequested;
        private int importsSinceRefresh;
        private Task refreshTask = Task.CompletedTask;
        private bool preferCustomArchiveURL;
        private bool disposed;

        internal event Action PopupEvent;
        internal event Action QueueUpdatedEvent;

        internal static readonly List<object> downloadQueue = new();
        private static readonly LinkedList<BeatSaberPlaylistsLib.Types.Playlist> coversToRefresh = new();

        private PopupContents _pendingPopup;
        internal PopupContents PendingPopup
        {
            get => _pendingPopup;
            private set
            {
                _pendingPopup = value;
                PopupEvent?.Invoke();
            }
        }

        public PlaylistSequentialDownloader(UBinder<Plugin, PluginMetadata> metadata)
        {
            // BeatSaverOptions parses System.Version, whereas BSIPA's version can contain build metadata.
            var options = new BeatSaverOptions(metadata.Value.Name, typeof(Plugin).Assembly.GetName().Version.ToString());
            beatSaverInstance = BeatSaverClientFactory.Create(options);
            archiveHttp = new PlaylistHttpService(options.BeatSaverAPI, options.ApplicationName + "/" + options.Version);
            PendingPopup = null;
        }

        public void Initialize()
        {
            SceneManager.activeSceneChanged += OnSceneChanged;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Loader.SongsLoadedEvent += OnSongsLoaded;
            foreach (var downloadQueueEntry in downloadQueue.OfType<DownloadQueueEntry>())
            {
                if (downloadQueueEntry.State == PlaylistDownloadState.Downloading)
                {
                    downloadQueueEntry.cancellationTokenSource.Dispose();
                    downloadQueueEntry.cancellationTokenSource = new CancellationTokenSource();
                    downloadQueueEntry.SetState(PlaylistDownloadState.Queued);
                }
                downloadQueueEntry.DownloadAbortedEvent += OnDownloadAborted;
                downloadQueueEntry.RetryRequestedEvent += OnRetryRequested;
            }

            StartQueue();

            PlaylistDownloader.PlaylistQueuedEvent += OnPlaylistQueued;
        }

        public void Dispose()
        {
            disposed = true;
            lifetime.Cancel();
            if (currentDownload != null && !currentDownload.cancellationTokenSource.IsCancellationRequested)
            {
                currentDownload.cancellationTokenSource.Cancel();
            }

            foreach (var downloadQueueEntry in downloadQueue.OfType<DownloadQueueEntry>())
            {
                downloadQueueEntry.DownloadAbortedEvent -= OnDownloadAborted;
                downloadQueueEntry.RetryRequestedEvent -= OnRetryRequested;
            }

            PlaylistDownloader.PlaylistQueuedEvent -= OnPlaylistQueued;
            Loader.SongsLoadedEvent -= OnSongsLoaded;
            SceneManager.activeSceneChanged -= OnSceneChanged;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            beatSaverInstance.Dispose();
            archiveHttp.Dispose();
        }

        public void QueuePlaylist(DownloadQueueEntry downloadQueueEntry)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PlaylistSequentialDownloader));
            if (downloadQueue.OfType<DownloadQueueEntry>().Any(e => ReferenceEquals(e.playlist, downloadQueueEntry.playlist))) return;
            downloadQueue.Add(downloadQueueEntry);
            OnPlaylistQueued(downloadQueueEntry);
        }

        private void OnPlaylistQueued(DownloadQueueEntry downloadQueueEntry)
        {
            downloadQueueEntry.DownloadAbortedEvent += OnDownloadAborted;
            downloadQueueEntry.RetryRequestedEvent += OnRetryRequested;
            QueueUpdatedEvent?.Invoke();
            StartQueue();
        }

        private void OnDownloadAborted(DownloadQueueEntry downloadQueueEntry)
        {
            downloadQueueEntry.DownloadAbortedEvent -= OnDownloadAborted;
            downloadQueueEntry.RetryRequestedEvent -= OnRetryRequested;
            downloadQueue.Remove(downloadQueueEntry);
            QueueUpdatedEvent?.Invoke();
        }

        private void OnRetryRequested(DownloadQueueEntry entry)
        {
            QueueUpdatedEvent?.Invoke();
            StartQueue();
        }

        private void StartQueue()
        {
            if (draining || disposed) return;
            draining = true;
            queueTask = DrainQueue();
        }

        private async Task DrainQueue()
        {
            try
            {
                while (!disposed)
                {
                    var entry = downloadQueue.OfType<DownloadQueueEntry>().FirstOrDefault(e => e.State == PlaylistDownloadState.Queued);
                    if (entry == null) break;
                    entry.SetState(PlaylistDownloadState.Downloading);
                    try
                    {
                        await DownloadPlaylist(entry);
                        if (!disposed && !entry.Aborted)
                        {
                            entry.SetState(PlaylistDownloadState.Completed);
                            entry.DownloadAbortedEvent -= OnDownloadAborted;
                            entry.RetryRequestedEvent -= OnRetryRequested;
                            downloadQueue.Remove(entry);
                        }
                    }
                    catch (OperationCanceledException) when (disposed || entry.Aborted) { }
                    catch (Exception) when (disposed || entry.Aborted) { }
                    catch (Exception ex)
                    {
                        entry.Fail(ex is TimeoutException ? "Timeout" : ex is PlaylistHttpException http ? (http.StatusCode == 404 ? "Map unavailable" : "Network or service")
                            : ex is InvalidSongPackageException ? "Invalid map archive" : ex is IOException ? "File or chart" : "Network or service");
                        Plugin.Log.Error($"Playlist download failed for {entry.PlaylistName}: {ex}");
                    }
                    finally
                    {
                        currentDownload = null;
                        PendingPopup = null;
                        if (!disposed) QueueUpdatedEvent?.Invoke();
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.Error($"Playlist queue failed: {ex}"); }
            finally
            {
                draining = false;
                if (!disposed && downloadQueue.OfType<DownloadQueueEntry>().All(e => e.State == PlaylistDownloadState.Failed)) OnQueueClear();
            }
        }

        internal void OnQueueClear()
        {
            if ((downloadQueue.Count == 0 || downloadQueue.OfType<DownloadQueueEntry>().All(e => e.State == PlaylistDownloadState.Failed)) &&
                Volatile.Read(ref importsSinceRefresh) > 0)
            {
                refreshRequested = true;
                TryRefreshSongs();
            }
        }

        private void OnSongsLoaded(Loader loader, ConcurrentDictionary<string, BeatmapLevel> beatmapLevels)
        {
            lock (ownedHashesGate) ownedHashes.Clear();
            foreach (var playlist in coversToRefresh)
            {
                playlist.RaiseCoverImageChangedForDefaultCover();
            }
            coversToRefresh.Clear();
            TryRefreshSongs();
        }

        private void OnSceneChanged(Scene previous, Scene current) => TryRefreshSongs();
        private void OnSceneUnloaded(Scene scene) => TryRefreshSongs();

        private bool CanRefreshSongs => !disposed && Loader.Instance != null && !Loader.AreSongsLoading &&
            SceneManager.GetActiveScene().name == "MainMenu" && !SceneManager.GetSceneByName("GameCore").isLoaded &&
            !SceneManager.GetSceneByName("GameplayCore").isLoaded;

        private void TryRefreshSongs()
        {
            if (!refreshRequested || !CanRefreshSongs || !refreshTask.IsCompleted) return;
            refreshTask = RefreshAfterTransition();
        }

        private async Task RefreshAfterTransition()
        {
            try
            {
                // Scene activation precedes HMUI's return animation. Require a
                // continuous quiet interval before updating the native pack views.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                DateTime? stableSince = null;
                while (DateTime.UtcNow < deadline)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (!CanRefreshSongs) return;
                    FlowCoordinator flow = Resources.FindObjectsOfTypeAll<MainFlowCoordinator>().FirstOrDefault();
                    bool stable = flow != null;
                    while (flow != null)
                    {
                        stable &= !flow.isInTransition;
                        flow = flow.childFlowCoordinator;
                    }
                    if (!stable) stableSince = null;
                    else if (!stableSince.HasValue) stableSince = DateTime.UtcNow;
                    else if (DateTime.UtcNow - stableSince.Value >= TimeSpan.FromSeconds(1)) break;
                    await Task.Delay(100, lifetime.Token);
                }
                if (!stableSince.HasValue || DateTime.UtcNow - stableSince.Value < TimeSpan.FromSeconds(1))
                {
                    Plugin.Log.Warn("Downloaded playlist songs remain pending while menu navigation is busy");
                    return;
                }
                if (!CanRefreshSongs) return;
                refreshRequested = false;
                Interlocked.Exchange(ref importsSinceRefresh, 0);
                Loader.Instance.RefreshSongs(false);
            }
            catch (OperationCanceledException) when (disposed) { }
            catch (Exception exception)
            {
                refreshRequested = true;
                Plugin.Log.Error("Cannot refresh downloaded playlist songs: " + exception);
            }
        }

        private async Task DownloadPlaylist(DownloadQueueEntry downloadQueueEntry)
        {
            currentDownload = downloadQueueEntry;
            HashSet<string> ownedSnapshot;
            lock (ownedHashesGate) ownedSnapshot = new HashSet<string>(ownedHashes, StringComparer.OrdinalIgnoreCase);
            var missingSongs = PlaylistLibUtils.GetMissingSongs(downloadQueueEntry.playlist, ownedSnapshot);
            downloadQueueEntry.SetMissingLevels(missingSongs.Count);
            downloadQueueEntry.SetTotalProgress(0);

            preferCustomArchiveURL = true;
            var shownCustomArchiveWarning = false;

            for (var i = 0; i < missingSongs.Count; i++)
            {
                using var mapDeadline = CancellationTokenSource.CreateLinkedTokenSource(downloadQueueEntry.cancellationTokenSource.Token, lifetime.Token);
                mapDeadline.CancelAfter(TimeSpan.FromSeconds(180));
                try
                {
                if (preferCustomArchiveURL && missingSongs[i].TryGetCustomData("customArchiveURL", out var outCustomArchiveURL))
                {
                    var customArchiveURL = (string)outCustomArchiveURL;
                    var identifier = PlaylistLibUtils.GetIdentifierForPlaylistSong(missingSongs[i]);
                    if (identifier == "")
                    {
                        throw new InvalidDataException("Playlist song has no downloadable identifier");
                    }

                    if (!shownCustomArchiveWarning)
                    {
                        shownCustomArchiveWarning = true;
                        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var popup = new YesNoPopupContents("This playlist uses mirror download links. Would you like to use them?", () => decision.TrySetResult(true),
                             noButtonPressedCallback: () => decision.TrySetResult(false), animateParentCanvas: false);
                        PendingPopup = popup;
                        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (mapDeadline.Token.Register(() => cancelled.TrySetCanceled()))
                        {
                            try
                            {
                                if (await Task.WhenAny(decision.Task, cancelled.Task) != decision.Task) mapDeadline.Token.ThrowIfCancellationRequested();
                                preferCustomArchiveURL = await decision.Task;
                            }
                            finally { if (ReferenceEquals(PendingPopup, popup)) PendingPopup = null; }
                        }

                        if (!preferCustomArchiveURL)
                        {
                            i--;
                            continue;
                        }
                    }
                    await BeatmapDownloadByCustomURL(customArchiveURL, identifier, mapDeadline.Token, downloadQueueEntry);
                }
                else if (!string.IsNullOrEmpty(missingSongs[i].Hash))
                {
                    await BeatmapDownloadByHash(missingSongs[i].Hash, mapDeadline.Token, downloadQueueEntry);
                }
                else if (!string.IsNullOrEmpty(missingSongs[i].Key))
                {
                    var hash = await BeatmapDownloadByKey(missingSongs[i].Key.ToLowerInvariant(), mapDeadline.Token, downloadQueueEntry);
                    if (!string.IsNullOrEmpty(hash))
                    {
                        missingSongs[i].Hash = hash;
                    }
                }
                else throw new InvalidDataException("Playlist song has neither a BeatSaver hash nor a key");

                downloadQueueEntry.SetTotalProgress(i + 1);

                if (downloadQueueEntry.Aborted)
                {
                    break;
                }

                if (disposed)
                {
                    return;
                }

                }
                catch (OperationCanceledException) when (!disposed && !downloadQueueEntry.Aborted)
                {
                    throw new TimeoutException("Map download exceeded 180 seconds: " + PlaylistLibUtils.GetIdentifierForPlaylistSong(missingSongs[i]));
                }
            }

            downloadQueueEntry.playlist.RaisePlaylistChanged();
            downloadQueueEntry.parentManager.StorePlaylist(downloadQueueEntry.playlist);

            if (downloadQueueEntry.playlist is BeatSaberPlaylistsLib.Types.Playlist playlist)
            {
                coversToRefresh.AddLast(playlist);
            }

            downloadQueueEntry.DownloadAbortedEvent -= OnDownloadAborted;
            currentDownload = null;
        }

        #region Map Download

        private async Task BeatSaverBeatmapDownload(Beatmap song, BeatmapVersion songversion, CancellationToken token, IProgress<double> progress = null)
        {
            var customSongsPath = CustomLevelPathHelper.customLevelsDirectoryPath;
            if (!Directory.Exists(customSongsPath))
            {
                Directory.CreateDirectory(customSongsPath);
            }

            bool owned;
            lock (ownedHashesGate) owned = ownedHashes.Contains(songversion.Hash);
            if (!owned)
            {
                var zip = await songversion.DownloadZIP(token, progress).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (zip == null || zip.Length == 0) throw new PlaylistHttpException("The map download returned no archive: " + songversion.Hash, 0, true);
                await ExtractZipAsync(zip, customSongsPath, FolderNameForBeatsaverMap(song), token).ConfigureAwait(false);
                lock (ownedHashesGate) ownedHashes.Add(songversion.Hash);
            }
        }

        private async Task<string> BeatmapDownloadByKey(string key, CancellationToken token, IProgress<double> progress = null)
        {
            if (!token.IsCancellationRequested)
            {
                try
                {
                    var song = await beatSaverInstance.Beatmap(key, token);
                    token.ThrowIfCancellationRequested();
                    if (song == null) throw new IOException("BeatSaver map was not found: " + key);
                    // A key is not enough to identify a specific version. So just get the latest one.
                    if (Loader.GetLevelByHash(song.LatestVersion.Hash) == null)
                    {
                        await BeatSaverBeatmapDownload(song, song.LatestVersion, token, progress);
                    }
                    return song.LatestVersion.Hash;
                }
                catch (Exception e)
                {
                    Plugin.Log.Error($"Failed to download Song {key}. Exception: {e}");
                    throw;
                }
            }
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Map download did not start");
        }

        private async Task BeatmapDownloadByHash(string hash, CancellationToken token, IProgress<double> progress = null)
        {
            if (!token.IsCancellationRequested)
            {
                try
                {
                    var song = await beatSaverInstance.BeatmapByHash(hash, token);
                    token.ThrowIfCancellationRequested();
                    if (song == null) throw new IOException("BeatSaver map was not found: " + hash);

                    BeatmapVersion matchingVersion = null;
                    foreach (var version in song.Versions)
                    {
                        if (string.Equals(hash, version.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingVersion = version;
                        }
                    }

                    if (matchingVersion != null)
                    {
                        await BeatSaverBeatmapDownload(song, matchingVersion, token, progress);
                    }
                    else
                    {
                        await BeatmapDownloadByCustomURL($"https://cdn.beatsaver.com/{hash.ToLowerInvariant()}.zip", FolderNameForBeatsaverMap(song), token, progress as IProgress<float>);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.Error($"Failed to download Song {hash}. Exception: {e}");
                    throw;
                }
            }
            else token.ThrowIfCancellationRequested();
        }

        private async Task BeatmapDownloadByCustomURL(string url, string songName, CancellationToken token, IProgress<float> progress = null)
        {
            if (!token.IsCancellationRequested)
            {
                try
                {
                    var customSongsPath = CustomLevelPathHelper.customLevelsDirectoryPath;
                    if (!Directory.Exists(customSongsPath))
                    {
                        Directory.CreateDirectory(customSongsPath);
                    }
                    var httpResponse = await archiveHttp.GetAsync(url, token, new FloatProgress(progress));
                    token.ThrowIfCancellationRequested();
                    if (httpResponse.Successful)
                    {
                        var zip = await httpResponse.ReadAsByteArrayAsync();
                        await ExtractZipAsync(zip, customSongsPath, songName, token).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new IOException("Archive request failed: " + url);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.Error($"Failed to download Song {url}: {e}");
                    throw;
                }
            }
            else token.ThrowIfCancellationRequested();
        }

        private string FolderNameForBeatsaverMap(Beatmap song)
        {
            // A workaround for the max path issue and long folder names
            var longFolderName = song.ID + " (" + song.Metadata.SongName + " - " + song.Metadata.LevelAuthorName;
            return longFolderName.Truncate(49, true) + ")";
        }

        private async Task ExtractZipAsync(byte[] zip, string customSongsPath, string songName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (zip == null || zip.Length == 0) throw new PlaylistHttpException("The map download returned no archive", 0, true);
            using var zipStream = new MemoryStream(zip);
            var folder = string.Concat(songName.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
            if (String.IsNullOrWhiteSpace(folder)) throw new InvalidDataException("Beatmap folder name is empty");
            if (PluginConfig.Instance.DriveFullProtection)
            {
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true);
                var driveInfo = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(customSongsPath)));
                if (driveInfo.AvailableFreeSpace - archive.Entries.Sum(e => e.Length) < 104857600)
                    throw new IOException("Less than 100 MB would remain after extracting the map");
            }
            zipStream.Position = 0;
            var stagingRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "playlist-mods", "downloads"));
            await Task.Run(() => PackageImporter.Install(zipStream, Path.GetFullPath(customSongsPath), folder, stagingRoot, token), token).ConfigureAwait(false);
            Interlocked.Increment(ref importsSinceRefresh);
        }

        private sealed class FloatProgress : IProgress<double>
        {
            private readonly IProgress<float> target;
            public FloatProgress(IProgress<float> target) { this.target = target; }
            public void Report(double value) => target?.Report((float)value);
        }

        #endregion
    }
}
