using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Automated asset conditioning & delivery pipeline for PoSoccer.
    ///
    /// Runs from the Unity menu ("PoSoccer/Process & Optimize Assets") or via
    /// headless CLI (`scripts/unity-exec.ps1 -Method PoSoccer.EditorTools.Editor_ProcessAssets.RunHeadless`):
    ///  1. Enforces mobile/WebGL texture compression (ASTC 6x6 on Android, Crunch on WebGL/Default)
    ///     on faces and pitch sprites without modifying LFS source binaries.
    ///  2. Enforces Vorbis streaming/preloading presets on Resources/Audio and Voices so
    ///     Android AAB and WebGL payloads stay lean and hitch-free.
    ///  3. Rebuilds PitchAtlas.spriteatlasv2 via Editor_BuildSpriteAtlases.
    ///  4. Verifies no PNG/WAV/ONNX binary is an unresolved Git LFS pointer stub.
    /// </summary>
    public static class Editor_ProcessAssets
    {
        [MenuItem("PoSoccer/Process & Optimize Assets")]
        public static void ProcessAll()
        {
            int texTouched = ConditionTextures();
            int audioTouched = ConditionAudio();
            Editor_BuildSpriteAtlases.Build();
            int stubs = CountLfsStubs();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Editor_ProcessAssets: conditioned {texTouched} textures, " +
                      $"{audioTouched} audio clips, rebuilt PitchAtlas, LFS stubs={stubs}.");
        }

        public static void RunHeadless()
        {
            ProcessAll();
            int stubs = CountLfsStubs();
            EditorApplication.Exit(stubs > 0 ? 1 : 0);
        }

        static int ConditionTextures()
        {
            int changed = 0;
            var dirs = new[] { "Assets/Art/Faces", "Assets/Sprites" };
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { dir });
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;

                    bool dirty = false;
                    bool isFace = path.Contains("/Faces/");
                    int targetMax = isFace ? 512 : 1024;
                    if (importer.maxTextureSize != targetMax)
                    {
                        importer.maxTextureSize = targetMax;
                        dirty = true;
                    }
                    if (!importer.crunchedCompression)
                    {
                        importer.crunchedCompression = true;
                        importer.compressionQuality = 75;
                        dirty = true;
                    }

                    var android = importer.GetPlatformTextureSettings("Android");
                    if (!android.overridden || android.format != TextureImporterFormat.ASTC_6x6)
                    {
                        android.overridden = true;
                        android.maxTextureSize = targetMax;
                        android.format = TextureImporterFormat.ASTC_6x6;
                        importer.SetPlatformTextureSettings(android);
                        dirty = true;
                    }

                    if (dirty)
                    {
                        importer.SaveAndReimport();
                        changed++;
                    }
                }
            }
            return changed;
        }

        static int ConditionAudio()
        {
            int changed = 0;
            var dirs = new[] { "Assets/Resources/Audio", "Assets/Resources/Voices" };
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { dir });
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (AssetImporter.GetAtPath(path) is not AudioImporter importer) continue;

                    string file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    bool longLoop = file.StartsWith("music") || file == "crowd_loop" || file == "crowd_swell";
                    var targetLoad = longLoop
                        ? AudioClipLoadType.Streaming
                        : AudioClipLoadType.DecompressOnLoad;

                    var sample = importer.defaultSampleSettings;
                    bool dirty = false;
                    if (sample.loadType != targetLoad || sample.compressionFormat != AudioCompressionFormat.Vorbis)
                    {
                        sample.loadType = targetLoad;
                        sample.compressionFormat = AudioCompressionFormat.Vorbis;
                        sample.quality = longLoop ? 0.65f : 0.75f;
                        importer.defaultSampleSettings = sample;
                        dirty = true;
                    }
                    if (!longLoop && !importer.forceToMono)
                    {
                        importer.forceToMono = true;
                        dirty = true;
                    }

                    if (dirty)
                    {
                        importer.SaveAndReimport();
                        changed++;
                    }
                }
            }
            return changed;
        }

        static int CountLfsStubs()
        {
            int stubs = 0;
            var extensions = new HashSet<string> { ".png", ".wav", ".onnx", ".ttf" };
            foreach (string file in Directory.EnumerateFiles("Assets", "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (!extensions.Contains(ext)) continue;
                var info = new FileInfo(file);
                if (info.Length > 256) continue;
                string head = File.ReadAllText(file);
                if (head.StartsWith("version https://git-lfs.github.com/spec/v1"))
                {
                    Debug.LogWarning($"Editor_ProcessAssets: LFS pointer stub detected at {file}");
                    stubs++;
                }
            }
            return stubs;
        }
    }
}
