using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using HMUI;
using System;
using System.Threading.Tasks;
using IPA.Loader;
using PlaylistManager.Downloaders;
using SiraUtil.Zenject;
using UnityEngine;
using Zenject;

namespace PlaylistManager.UI
{
    public class PlaylistDownloaderViewController : MonoBehaviour, IInitializable, IDisposable
    {
        private PlaylistSequentialDownloader playlistDownloader;
        private PopupModalsController popupModalsController;
        private PluginMetadata pluginMetadata;
        private BSMLParser bsmlParser;
        private bool parsed;
        private bool disposed;
        private PlaylistManager.Types.PopupContents shownDownloadPopup;
        private Task popupUiTask = Task.CompletedTask;

        [UIComponent("download-list")]
        private readonly CustomCellListTableData customListTableData;

        [UIComponent("root")]
        private readonly RectTransform rootTransform;

        [Inject]
        internal void Construct(PlaylistSequentialDownloader playlistDownloader, PopupModalsController popupModalsController, UBinder<Plugin, PluginMetadata> pluginMetadata, BSMLParser bsmlParser)
        {
            this.playlistDownloader = playlistDownloader;
            this.popupModalsController = popupModalsController;
            this.pluginMetadata = pluginMetadata.Value;
            this.bsmlParser = bsmlParser;
        }

        public void SetParent(Transform parent, Vector3? scale = null)
        {
            if (!parsed)
            {
                bsmlParser.Parse(BeatSaberMarkupLanguage.Utilities.GetResourceContent(pluginMetadata.Assembly, "PlaylistManager.UI.Views.PlaylistDownloaderView.bsml"), parent.gameObject, this);
            }
            rootTransform.SetParent(parent, false);
            rootTransform.localScale = scale ?? Vector3.one;
            OnPopupRequested();
        }

        public void OnDisable()
        {
            if (playlistDownloader.PendingPopup != null)
            {
                popupModalsController.HideYesNoModal();
            }
        }

        public void Initialize()
        {
            playlistDownloader.PopupEvent += OnPopupRequested;
            playlistDownloader.QueueUpdatedEvent += UpdateQueue;
        }

        public void Dispose()
        {
            disposed = true;
            playlistDownloader.PopupEvent -= OnPopupRequested;
            playlistDownloader.QueueUpdatedEvent -= UpdateQueue;
        }

        [UIAction("#post-parse")]
        private void PostParse()
        {
            parsed = true;
            transform.SetParent(rootTransform);
            customListTableData.Data = PlaylistSequentialDownloader.downloadQueue;
            customListTableData.TableView.ReloadDataKeepingPosition();
        }

        private void OnPopupRequested()
        {
            if (!disposed && parsed)
            {
                popupUiTask = IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                {
                    try { RefreshPendingPopup(); }
                    catch (Exception exception) { Plugin.Log.Error("Cannot update playlist download confirmation: " + exception); }
                });
            }
        }

        private void RefreshPendingPopup()
        {
            var popup = playlistDownloader.PendingPopup;
            if (disposed || !parsed || rootTransform == null) return;
            if (popup == null)
            {
                if (shownDownloadPopup != null) popupModalsController.HideYesNoModal();
                shownDownloadPopup = null;
                return;
            }
            popup.parent = rootTransform;
            popup.animateParentCanvas = !rootTransform.GetComponentInParent<ModalView>();
            if (rootTransform.gameObject.activeInHierarchy)
            {
                popupModalsController.ShowModal(popup);
                shownDownloadPopup = popup;
            }
        }

        private void UpdateQueue()
        {
            if (customListTableData != null)
            {
                customListTableData.TableView.ReloadDataKeepingPosition();
            }
        }
    }
}
