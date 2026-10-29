using System;
using System.Collections.Generic;
using BeatSaberPlaylistsLib.Types;
using PlaylistManager.Utilities;
using HMUI;
using PlaylistManager.Configuration;
using PlaylistManager.UI;
using SiraUtil.Affinity;
using UnityEngine;

/*
 * Original Author: Auros
 * Taken from PlaylistCore
 */

namespace PlaylistManager.AffinityPatches
{
    // TODO: Fix lag when loading covers.
    // TODO: Don't load covers that have no size.
    internal class LevelCollectionCellSetDataPatch : IAffinity
    {
        private readonly Dictionary<IPlaylist, AnnotatedBeatmapLevelCollectionCell> eventTable = new();
        private readonly HoverHintController hoverHintController;
        private readonly PlaylistUpdater playlistUpdater;

        public LevelCollectionCellSetDataPatch(HoverHintController hoverHintController, PlaylistUpdater playlistUpdater)
        {
            this.hoverHintController = hoverHintController;
            this.playlistUpdater = playlistUpdater;
        }

        [AffinityPatch(typeof(AnnotatedBeatmapLevelCollectionCell), nameof(AnnotatedBeatmapLevelCollectionCell.SetData))]
        private void Patch(AnnotatedBeatmapLevelCollectionCell __instance, ref BeatmapLevelPack beatmapLevelPack)
        {
            if (beatmapLevelPack is PlaylistLevelPack playlistLevelPack)
            {
                var playlist = playlistLevelPack.playlist;
                eventTable.Remove(playlist);
                eventTable.Add(playlist, __instance);
                playlist.SpriteLoaded -= OnSpriteLoaded;
                playlist.SpriteLoaded += OnSpriteLoaded;
            }

            UpdateHoverHint(__instance, beatmapLevelPack);
        }

        internal void RefreshHoverHints()
        {
            foreach (var cell in Resources.FindObjectsOfTypeAll<AnnotatedBeatmapLevelCollectionCell>())
            {
                if (cell.gameObject.scene.IsValid()) UpdateHoverHint(cell, cell._beatmapLevelPack);
            }
        }

        private void UpdateHoverHint(AnnotatedBeatmapLevelCollectionCell cell, BeatmapLevelPack pack)
        {
            var ownedHint = cell.GetComponent<PlaylistNameHoverHint>();
            if (!PluginConfig.Instance.PlaylistHoverHints || pack == null)
            {
                // OnDisable dismisses an already visible native tooltip immediately.
                if (ownedHint != null) ownedHint.enabled = false;
                return;
            }

            if (ownedHint == null)
            {
                // Leave components supplied by the game or another mod untouched.
                if (cell.GetComponent<HoverHint>() != null) return;
                ownedHint = cell.gameObject.AddComponent<PlaylistNameHoverHint>();
                HoverHint hint = ownedHint;
                Accessors.HoverHintControllerAccessor(ref hint) = hoverHintController;
            }

            ownedHint.text = pack.packName;
            ownedHint.enabled = true;
        }

        private void OnSpriteLoaded(object sender, EventArgs e)
        {
            // TODO: Figure out why this doesn't seem to happen.
            if (sender is not IPlaylist playlist)
            {
                return;
            }

            playlist.SpriteLoaded -= OnSpriteLoaded;

            if (!eventTable.TryGetValue(playlist, out var tableCell) || tableCell == null || tableCell._beatmapLevelPack is not PlaylistLevelPack)
            {
                return;
            }

            tableCell._coverImage.sprite = playlist.SmallSprite;
            // TODO: Figure out why this needs to be done here and in UpdatePlaylist when switching covers. Worth noting that this event is invoked twice as well.
            playlistUpdater.RefreshAnnotatedBeatmapCollection(tableCell._beatmapLevelPack);
        }
    }

    internal sealed class PlaylistNameHoverHint : HoverHint
    {
    }
}
