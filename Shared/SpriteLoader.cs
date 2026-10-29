using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace PlaylistMods.Common
{
    // Existing controllers require their embedded artwork during synchronous construction.
    // All callers run on Unity's main thread; arbitrary user covers are not cached here.
    internal static class SpriteLoader
    {
        internal static Sprite FromResource(Assembly assembly, string name)
        {
            string key = assembly.GetName().Name + ":" + name;
            if (BeatSaberMarkupLanguage.Utilities.SpriteCache.TryGetValue(key, out var cached) && cached) return cached;
            using (var stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new FileNotFoundException("Embedded image is missing", name);
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    var sprite = FromBytes(buffer.ToArray());
                    sprite.name = name;
                    BeatSaberMarkupLanguage.Utilities.SpriteCache[key] = sprite;
                    return sprite;
                }
            }
        }

        internal static Sprite FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("Image data is empty", nameof(bytes));
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, bytes, false))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException("Image data could not be decoded");
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            return BeatSaberMarkupLanguage.Utilities.LoadSpriteFromTexture(texture);
        }
    }
}
