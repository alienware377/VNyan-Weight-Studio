using System;
using System.IO;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Opens a VSeeFace / VNyan <c>.vsfavatar</c> file (a standard Unity "UnityFS"
    /// AssetBundle) and pulls the avatar prefab out of it so a fully standalone .vbx
    /// can be built straight from the file, no dependency on VNyan having that avatar
    /// loaded, and nothing left behind in the live scene.
    ///
    /// We read by momentarily instantiating the prefab (parked far off-screen, hidden,
    /// DontSave) only long enough for the exporter to copy what it needs into managed
    /// memory, then we destroy the instance and unload the bundle. Because the copy is
    /// transient and never parented into VNyan's avatar hierarchy, it does not duplicate
    /// meshes/GameObjects in the running scene or bog down resources after export.
    ///
    /// IMPORTANT: the instance's renderers still reference the bundle's shared Mesh /
    /// Material / Texture assets, so callers MUST finish reading (VbxExporter.Build,
    /// which copies vertices + bakes textures to PNG) BEFORE calling <see cref="Cleanup"/>,
    /// which unloads the bundle and frees those assets.
    /// </summary>
    internal static class VsfAvatarReader
    {
        public sealed class Loaded
        {
            public GameObject instance;   // temporary clone to read from
            public AssetBundle bundle;    // the bundle the prefab came from
            public bool ownsBundle;       // true => we opened it and must Unload it
            public string sourceName;     // prefab name (for default output file name)
        }

        /// <summary>
        /// Load the avatar prefab from <paramref name="vsfPath"/> and instantiate a
        /// temporary read copy. Returns null if no avatar prefab could be found.
        /// </summary>
        public static Loaded Load(string vsfPath)
        {
            if (string.IsNullOrEmpty(vsfPath) || !File.Exists(vsfPath)) return null;

            AssetBundle bundle = null;
            bool owns = false;

            // Try to open the file ourselves (reading the bytes first avoids leaving an
            // OS file lock on the .vsfavatar).
            try
            {
                byte[] bytes = File.ReadAllBytes(vsfPath);
                bundle = AssetBundle.LoadFromMemory(bytes);
                owns = bundle != null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WeightStudio] LoadFromMemory failed (" + e.Message +
                                 "); will look for an already-loaded copy.");
                bundle = null;
            }

            GameObject prefab = null;
            if (bundle != null)
            {
                prefab = FindAvatarPrefab(bundle);
                if (prefab == null) { bundle.Unload(true); bundle = null; owns = false; }
            }

            // Unity refuses to load a bundle whose serialized files are already loaded
            // (e.g. this very avatar is the one live in VNyan). In that case reuse the
            // already-loaded bundle instead of fighting it.
            if (prefab == null)
            {
                foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
                {
                    GameObject p = FindAvatarPrefab(b);
                    if (p != null) { prefab = p; bundle = b; owns = false; break; }
                }
            }

            if (prefab == null) return null;

            GameObject inst = UnityEngine.Object.Instantiate(prefab);
            inst.name = prefab.name;
            inst.hideFlags = HideFlags.HideAndDontSave;
            // Do NOT move the instance: the exporter reads each bone's LOCAL transform,
            // and the root bone has no parent, so any world offset here would be baked
            // straight into the .vbx (and the avatar would render far off-screen when
            // loaded standalone). Keep the authored rest pose intact and instead just
            // hide the clone by disabling its renderers: invisibility without touching
            // any data the exporter reads. (Export is synchronous and the clone is
            // DestroyImmediate'd before any frame renders anyway; this is belt-and-braces.)
            foreach (var rend in inst.GetComponentsInChildren<Renderer>(true))
                if (rend != null) rend.enabled = false;
            return new Loaded { instance = inst, bundle = bundle, ownsBundle = owns, sourceName = prefab.name };
        }

        /// <summary>Destroy the temporary instance and (if we opened it) unload the bundle.</summary>
        public static void Cleanup(Loaded loaded)
        {
            if (loaded == null) return;
            if (loaded.instance != null)
            {
                // immediate so no stray frame can render the parked copy
                UnityEngine.Object.DestroyImmediate(loaded.instance);
                loaded.instance = null;
            }
            if (loaded.ownsBundle && loaded.bundle != null)
            {
                loaded.bundle.Unload(true);
                loaded.bundle = null;
            }
        }

        /// <summary>
        /// Pick the avatar root prefab inside a bundle: the GameObject whose hierarchy
        /// holds the most SkinnedMeshRenderers, strongly preferring one that also has an
        /// Animator (the actual avatar root rather than a sub-part).
        /// </summary>
        static GameObject FindAvatarPrefab(AssetBundle bundle)
        {
            if (bundle == null || bundle.isStreamedSceneAssetBundle) return null;
            GameObject best = null;
            int bestScore = 0;
            try
            {
                foreach (var go in bundle.LoadAllAssets<GameObject>())
                {
                    if (go == null) continue;
                    var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    int score = (smrs != null) ? smrs.Length : 0;
                    if (score == 0) continue;
                    if (go.GetComponentInChildren<Animator>(true) != null) score += 1000;
                    if (score > bestScore) { bestScore = score; best = go; }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WeightStudio] Scanning bundle for avatar prefab failed: " + e.Message);
            }
            return best;
        }
    }
}
