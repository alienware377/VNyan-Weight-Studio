using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using VNyanWeightStudio;   // shared VBX format namespace

namespace VNyanWeightStudioReader
{
    /// <summary>
    /// VNyan SDK plugin: "Weight Studio Reader". Lists .vbx avatars exported by the
    /// Weight Studio editor plugin and loads them back into the scene with their
    /// full (unlimited) bone-weight data intact.
    ///
    /// The window prefab is built by WeightStudioReaderBuild.cs (Editor) and shipped
    /// as a .vnobj AssetBundle; this script wires it by control name at runtime.
    ///
    /// Control-name contract the prefab must provide (found via Find&lt;T&gt;):
    ///   Texts:   Title, Label_Status
    ///   Buttons: Button_Rescan, Button_LoadSel, Button_RemoveLast, Button_OpenFolder, Button_Close
    ///   ScrollRect content: FileContent
    /// </summary>
    public class WeightStudioReaderPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        public GameObject windowPrefab;

        GameObject window;
        Text statusText;
        Font uiFont;

        RectTransform fileContent;
        readonly List<string> files = new List<string>();
        string selectedPath;

        readonly List<GameObject> loaded = new List<GameObject>();
        // For each loaded VBX we remember the live avatar we replaced (for status) and
        // the exact renderers we disabled on it, so RemoveLast can bring them back.
        readonly List<GameObject> hiddenFor = new List<GameObject>();
        readonly List<Renderer[]> hiddenRenderers = new List<Renderer[]>();
        // Per loaded copy: blendshape mirror links so VNyan-driven expressions on the
        // (hidden) live renderers are copied onto the visible copy every frame.
        readonly List<List<MirrorPair>> mirrorFor = new List<List<MirrorPair>>();

        // Links one hidden source renderer to its visible copy + a precomputed
        // copyShapeIndex -> sourceShapeIndex table (matched by blendshape name).
        sealed class MirrorPair
        {
            public SkinnedMeshRenderer src;
            public SkinnedMeshRenderer dst;
            public int[] shapeMap;
        }

        public void Awake()
        {
            // PROBE: log VNyan's BOOT-DEFAULT skin-weights quality before any plugin code
            // touches it. If this reads "Unlimited", a plain >4-weight .vsfavatar would
            // render correctly with no plugin at all. If it reads "FourBones"/etc., the
            // host caps skinning at 4 unless something flips this global at runtime.
            Debug.Log("[WeightStudioReader] PROBE QualitySettings.skinWeights at boot = " +
                      QualitySettings.skinWeights + "  (level=" + QualitySettings.GetQualityLevel() + ")");

            VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton("Weight Studio Reader", this);
            window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(windowPrefab);
            uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (window != null)
            {
                window.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
                window.SetActive(false);
                WireUI();
            }
        }

        public void pluginButtonClicked()
        {
            if (window == null) return;
            window.SetActive(!window.activeSelf);
            if (window.activeSelf)
            {
                window.transform.SetAsLastSibling();
                RescanFiles();
            }
        }

        // ------------------------------------------------------------ folders

        // Mirror the editor plugin's output location so exported files show up here
        // automatically; also scan a couple of likely manual-drop spots.
        IEnumerable<string> ScanDirs()
        {
            yield return Path.Combine(Application.persistentDataPath, "WeightStudio");
            yield return Path.Combine(Application.persistentDataPath, "WeightStudioReader");
        }

        string PrimaryDir()
        {
            string d = Path.Combine(Application.persistentDataPath, "WeightStudio");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        // ------------------------------------------------------------ UI wiring

        void WireUI()
        {
            BindButton("Button_Rescan", RescanFiles);
            BindButton("Button_LoadSel", LoadSelected);
            BindButton("Button_RemoveLast", RemoveLast);
            BindButton("Button_OpenFolder", OpenFolder);
            BindButton("Button_Close", () => { if (window != null) window.SetActive(false); });

            fileContent = Find<RectTransform>("FileContent");
            statusText = Find<Text>("Label_Status");

            RescanFiles();
            SetStatus("Select a .vbx file and click Load.");
        }

        void RescanFiles()
        {
            files.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in ScanDirs())
            {
                if (!Directory.Exists(dir)) continue;
                string[] found;
                try { found = Directory.GetFiles(dir, "*.vbx", SearchOption.TopDirectoryOnly); }
                catch { continue; }
                foreach (string f in found)
                    if (seen.Add(f)) files.Add(f);
            }
            // newest first
            files.Sort((a, b) =>
            {
                DateTime ta = SafeWriteTime(a), tb = SafeWriteTime(b);
                return tb.CompareTo(ta);
            });

            if (selectedPath != null && !files.Contains(selectedPath)) selectedPath = null;
            if (selectedPath == null && files.Count > 0) selectedPath = files[0];

            RebuildFileList();
        }

        static DateTime SafeWriteTime(string p) { try { return File.GetLastWriteTime(p); } catch { return DateTime.MinValue; } }

        void RebuildFileList()
        {
            if (fileContent == null) return;
            for (int i = fileContent.childCount - 1; i >= 0; i--) Destroy(fileContent.GetChild(i).gameObject);

            const float rowH = 22f;
            float y = 0f;
            foreach (string path in files)
            {
                string captured = path;
                string label = Path.GetFileName(path);
                MakeRow(fileContent, "File_" + label, label, y, rowH,
                    () => { selectedPath = captured; RebuildFileList(); SetStatus("Selected: " + label); },
                    () => string.Equals(selectedPath, captured, StringComparison.OrdinalIgnoreCase));
                y += rowH;
            }
            if (files.Count == 0)
                MakeInfo(fileContent, "(no .vbx files — export one from Weight Studio first)", y, rowH);
            fileContent.sizeDelta = new Vector2(0f, Mathf.Max(y, 1f));
        }

        // ------------------------------------------------------------ actions

        void LoadSelected()
        {
            if (string.IsNullOrEmpty(selectedPath) || !File.Exists(selectedPath))
            {
                SetStatus("No file selected (Rescan?).");
                return;
            }
            try
            {
                // Find the live VNyan avatar BEFORE we add ours to the scene, so we can
                // (a) borrow its real materials (correct MToon/VRM shaders the VBX can't
                // carry) and (b) hide it so the loaded copy replaces it instead of
                // duplicating it.
                GameObject original = FindLiveAvatar(null);

                // Standalone (no live avatar to borrow real materials from): prefer the
                // REAL compiled materials embedded in the .vbx (the original .vsfavatar
                // bundle — gives faithful locked Poiyomi etc.). Where that's unavailable,
                // fall back to reconstructing from the v3 property capture (exact shader
                // if present in VNyan, else remapped to the closest available substitute).
                // Only enable the embedded-bundle path when standalone, so we never
                // double-load a .vsfavatar VNyan already has open.
                VbxImporter.ForceVisibleMaterials = false;
                VbxImporter.PreferEmbeddedBundle = (original == null);
                GameObject go;
                try { go = VbxImporter.Import(selectedPath); }
                finally { VbxImporter.PreferEmbeddedBundle = false; }
                if (go != null)
                {
                    Renderer[] hid = null;
                    List<MirrorPair> mirror = null;
                    if (original != null)
                    {
                        int borrowed = BorrowMaterials(go, original.transform);
                        // Re-skin our meshes onto the LIVE avatar's bones (same rig, matched
                        // by name) so they're driven by VNyan's animation/tracking. Then we
                        // don't need (and shouldn't keep) our own dead skeleton + Animator.
                        int rebound = RebindToLiveSkeleton(go, original.transform);
                        var an = go.GetComponent<Animator>();
                        if (an != null) Destroy(an);
                        // Link blendshapes so VNyan's expressions/visemes/blinks (which it
                        // writes to the original renderers) get mirrored onto the copy.
                        mirror = BuildMirror(go, original.transform);
                        // Hide only the original's RENDERERS — keep its GameObject active so
                        // VNyan keeps animating the shared bones our meshes ride on.
                        hid = HideRenderers(original);
                        Debug.Log("[WeightStudioReader] Replaced live avatar '" + original.name +
                                  "': borrowed " + borrowed + " material slot(s), rebound " +
                                  rebound + " bone(s), mirrored " + (mirror != null ? mirror.Count : 0) +
                                  " mesh(es), hid " + (hid != null ? hid.Length : 0) + " renderer(s).");
                    }
                    else
                    {
                        // Standalone: no live avatar to ride. Place at (near) origin and
                        // make sure nothing is left in an invisible state, then log a full
                        // diagnostic so we can see WHY a model might not show.
                        go.transform.position = new Vector3(loaded.Count * 0.75f, 0f, 0f);
                        go.transform.rotation = Quaternion.identity;
                        go.transform.localScale = Vector3.one;
                        EnsureVisible(go);
                        LogStandaloneDiagnostics(go);
                        StartCoroutine(LogDeferredDiagnostics(go));
                    }

                    loaded.Add(go);
                    hiddenFor.Add(original);   // may be null (nothing was replaced)
                    hiddenRenderers.Add(hid);
                    mirrorFor.Add(mirror);

                    int verts = 0, maxInf = 0;
                    foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                        if (smr.sharedMesh != null)
                        {
                            verts += smr.sharedMesh.vertexCount;
                            var bpv = smr.sharedMesh.GetBonesPerVertex();
                            for (int i = 0; i < bpv.Length; i++) if (bpv[i] > maxInf) maxInf = bpv[i];
                        }
                    if (original != null)
                        SetStatus("Loaded '" + go.name + "': " + verts + " verts, up to " + maxInf +
                                  " bones/vertex.  Attached to your live avatar — it moves with tracking and keeps its shaders.");
                    else
                        SetStatus("STATIC PREVIEW of '" + go.name + "': " + verts + " verts, up to " + maxInf +
                                  " bones/vertex.  No avatar is loaded, so VNyan can't track this. " +
                                  "To VTube with it: load your avatar (.vsfavatar/.vrm) in VNyan FIRST, then Load this .vbx again.");
                    Debug.Log("[WeightStudioReader] Loaded " + selectedPath);
                }
                else SetStatus("Load returned nothing.");
            }
            catch (Exception e)
            {
                Debug.LogError("[WeightStudioReader] Load failed: " + e);
                SetStatus("Load failed (see log): " + e.Message);
            }
        }

        void RemoveLast()
        {
            for (int i = loaded.Count - 1; i >= 0; i--)
            {
                Renderer[] hid = i < hiddenRenderers.Count ? hiddenRenderers[i] : null;
                bool hadObj = loaded[i] != null;
                string n = hadObj ? loaded[i].name : null;
                if (hadObj) Destroy(loaded[i]);
                loaded.RemoveAt(i);
                if (i < hiddenFor.Count) hiddenFor.RemoveAt(i);
                if (i < hiddenRenderers.Count) hiddenRenderers.RemoveAt(i);
                if (i < mirrorFor.Count) mirrorFor.RemoveAt(i);
                RestoreRenderers(hid);   // un-hide the live avatar we replaced
                if (hadObj)
                {
                    SetStatus(hid != null && hid.Length > 0
                        ? "Removed '" + n + "' and restored the live avatar."
                        : "Removed '" + n + "'.");
                    return;
                }
            }
            SetStatus("Nothing loaded to remove.");
        }

        // Disable (but don't deactivate) the avatar's mesh renderers, returning the ones
        // we actually turned off so they can be restored. Keeping the GameObject ACTIVE is
        // what lets VNyan keep animating the bones our re-skinned meshes ride on.
        static Renderer[] HideRenderers(GameObject avatar)
        {
            var list = new List<Renderer>();
            foreach (var r in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (r.enabled) { r.enabled = false; list.Add(r); }
            }
            return list.ToArray();
        }

        static void RestoreRenderers(Renderer[] hid)
        {
            if (hid == null) return;
            foreach (var r in hid) if (r != null) r.enabled = true;
        }

        // Standalone safety net: force every renderer on, allow >4 influences, and make
        // sure the GameObject + all parents are active so nothing silently hides the model.
        static void EnsureVisible(GameObject go)
        {
            go.SetActive(true);
            QualitySettings.skinWeights = SkinWeights.Unlimited;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null) continue;
                smr.enabled = true;
                smr.gameObject.SetActive(true);
                smr.updateWhenOffscreen = true;          // never frustum-cull it away
                smr.allowOcclusionWhenDynamic = false;
                if (smr.sharedMesh != null) smr.localBounds = smr.sharedMesh.bounds;
            }
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                if (mr != null) { mr.enabled = true; mr.gameObject.SetActive(true); }
        }

        // Dump everything we'd need to explain an invisible standalone load: world position
        // of the root, combined renderer bounds, per-renderer vertex/material/shader state.
        static void LogStandaloneDiagnostics(GameObject go)
        {
            try
            {
                var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Bounds combined = default(Bounds);
                bool haveBounds = false;
                int rendererCount = 0, enabledCount = 0, materialCount = 0;
                var shaderNames = new HashSet<string>(StringComparer.Ordinal);
                var fallbackShaders = new HashSet<string>(StringComparer.Ordinal);

                foreach (var smr in smrs)
                {
                    if (smr == null) continue;
                    rendererCount++;
                    if (smr.enabled && smr.gameObject.activeInHierarchy) enabledCount++;
                    Bounds b = smr.bounds;   // world-space
                    if (!haveBounds) { combined = b; haveBounds = true; }
                    else combined.Encapsulate(b);
                    var mats = smr.sharedMaterials;
                    if (mats != null)
                        foreach (var m in mats)
                        {
                            if (m == null) continue;
                            materialCount++;
                            string sn = m.shader != null ? m.shader.name : "(null shader)";
                            shaderNames.Add(sn);
                            // a fallback to Standard/URP/Sprites usually means the avatar's
                            // real shader wasn't present in VNyan at load time
                            if (sn == "Standard" || sn == "Universal Render Pipeline/Lit" ||
                                sn == "Sprites/Default" || sn == "Hidden/InternalErrorShader")
                                fallbackShaders.Add(sn);
                        }
                }

                Debug.Log("[WeightStudioReader][standalone] '" + go.name + "' rootPos=" +
                          go.transform.position + " scale=" + go.transform.lossyScale +
                          " renderers=" + rendererCount + " (enabled " + enabledCount + ")" +
                          " materials=" + materialCount);
                if (haveBounds)
                    Debug.Log("[WeightStudioReader][standalone] combined world bounds center=" +
                              combined.center + " size=" + combined.size);
                else
                    Debug.LogWarning("[WeightStudioReader][standalone] NO renderer bounds — geometry/bones may be degenerate.");
                Debug.Log("[WeightStudioReader][standalone] shaders: " + string.Join(", ", new List<string>(shaderNames).ToArray()));
                if (fallbackShaders.Count > 0)
                    Debug.LogWarning("[WeightStudioReader][standalone] some materials fell back to generic shaders (" +
                                     string.Join(", ", new List<string>(fallbackShaders).ToArray()) +
                                     ") — the avatar's original shader was not found in VNyan; colors/visibility may be wrong.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WeightStudioReader][standalone] diagnostics failed: " + e.Message);
            }
        }

        // Runs a couple of frames after load, when Renderer.isVisible and camera culling
        // have actually been evaluated. This is what tells us whether VNyan's camera is
        // even attempting to draw our meshes (culling/layer problem) versus drawing them
        // but producing nothing (shader/material problem).
        IEnumerator LogDeferredDiagnostics(GameObject go)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            if (go == null) yield break;
            try
            {
                // ALL cameras (including disabled / inactive ones VNyan may drive manually)
                foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
                {
                    if (cam == null) continue;
                    Transform ct = cam.transform;
                    Debug.Log("[WeightStudioReader][cam] '" + cam.name + "' enabled=" + cam.enabled +
                              " active=" + cam.gameObject.activeInHierarchy +
                              " depth=" + cam.depth + " mask=0x" + cam.cullingMask.ToString("X8") +
                              " target=" + (cam.targetTexture != null ? cam.targetTexture.name : "screen") +
                              " pos=" + ct.position + " fwd=" + ct.forward + " fov=" + cam.fieldOfView);
                }

                // named layers (so we can read VNyan's avatar layer scheme)
                var named = new List<string>();
                for (int l = 0; l < 32; l++)
                {
                    string ln = LayerMask.LayerToName(l);
                    if (!string.IsNullOrEmpty(ln)) named.Add(l + ":'" + ln + "'");
                }
                Debug.Log("[WeightStudioReader][layers] " + string.Join("  ", named.ToArray()));

                // per-renderer visibility + layer + individual world bounds
                int visible = 0, total = 0;
                var layers = new HashSet<int>();
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    total++;
                    layers.Add(r.gameObject.layer);
                    if (r.isVisible) visible++;
                    Debug.Log("[WeightStudioReader][rend] '" + r.name + "' vis=" + r.isVisible +
                              " enabled=" + r.enabled + " layer=" + r.gameObject.layer +
                              " bCenter=" + r.bounds.center + " bSize=" + r.bounds.size);
                }
                Debug.Log("[WeightStudioReader][vis] renderers=" + total + " isVisible=" + visible +
                          " rootLayer=" + go.layer + "('" + LayerMask.LayerToName(go.layer) + "')" +
                          " rendererLayers=[" + string.Join(",", LayerList(layers)) + "]");

                // what MToon types actually exist in VNyan right now?
                int mtoonHits = 0;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types = null;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t == null || t.FullName == null) continue;
                        if (t.FullName.IndexOf("MToon", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            t.FullName.IndexOf("Utils", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            mtoonHits++;
                            Debug.Log("[WeightStudioReader][mtoon-type] " + t.FullName + "  (asm " + asm.GetName().Name + ")");
                        }
                    }
                }
                if (mtoonHits == 0)
                    Debug.Log("[WeightStudioReader][mtoon-type] none found containing 'MToon'+'Utils'.");

                // is the MToon shader actually drawable, or a stripped variant?
                Shader mt = Shader.Find("VRM/MToon");
                Debug.Log("[WeightStudioReader][shader] Shader.Find(\"VRM/MToon\")=" +
                          (mt != null ? ("'" + mt.name + "' supported=" + mt.isSupported) : "NULL") +
                          " ; Standard supported=" + (Shader.Find("Standard") != null));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WeightStudioReader][deferred] diagnostics failed: " + e.Message);
            }
        }

        static string[] LayerList(HashSet<int> layers)
        {
            var outp = new List<string>();
            foreach (var l in layers) outp.Add(l + "('" + LayerMask.LayerToName(l) + "')");
            return outp.ToArray();
        }

        // Repoint each imported SkinnedMeshRenderer's bones[] / rootBone at the LIVE
        // avatar's transforms of the same name. The VBX came from this very avatar, so
        // the rig + rest pose + bindposes match — assigning the live bones makes the
        // meshes deform with VNyan's animated skeleton. Returns how many bones rebound.
        int RebindToLiveSkeleton(GameObject loadedGo, Transform liveRoot)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var t in liveRoot.GetComponentsInChildren<Transform>(true))
                if (!map.ContainsKey(t.name)) map[t.name] = t;

            int rebound = 0;
            foreach (var smr in loadedGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = smr.bones;
                if (bones != null && bones.Length > 0)
                {
                    var nb = new Transform[bones.Length];
                    for (int i = 0; i < bones.Length; i++)
                    {
                        Transform live;
                        if (bones[i] != null && map.TryGetValue(bones[i].name, out live)) { nb[i] = live; rebound++; }
                        else nb[i] = bones[i];
                    }
                    smr.bones = nb;
                }
                Transform lr;
                if (smr.rootBone != null && map.TryGetValue(smr.rootBone.name, out lr)) smr.rootBone = lr;
                smr.updateWhenOffscreen = true;   // don't cull while it tracks the live rig
            }
            return rebound;
        }

        // ----------------------------------------------------------- blendshape mirror

        // Pair each copy renderer with its source (live) renderer and precompute a
        // copyShapeIndex -> sourceShapeIndex table by blendshape name (the copy came from
        // the same mesh data, so names match exactly).
        List<MirrorPair> BuildMirror(GameObject loadedGo, Transform liveRoot)
        {
            var srcByName = new Dictionary<string, SkinnedMeshRenderer>(StringComparer.Ordinal);
            var srcByMesh = new Dictionary<string, SkinnedMeshRenderer>(StringComparer.Ordinal);
            foreach (var s in liveRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (s == null || s.sharedMesh == null) continue;
                if (!srcByName.ContainsKey(s.gameObject.name)) srcByName[s.gameObject.name] = s;
                string mk = s.sharedMesh.name;
                if (!string.IsNullOrEmpty(mk) && !srcByMesh.ContainsKey(mk)) srcByMesh[mk] = s;
            }

            var pairs = new List<MirrorPair>();
            foreach (var dst in loadedGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (dst == null || dst.sharedMesh == null || dst.sharedMesh.blendShapeCount == 0) continue;
                SkinnedMeshRenderer src;
                if (!srcByName.TryGetValue(dst.gameObject.name, out src))
                    srcByMesh.TryGetValue(dst.sharedMesh.name, out src);
                if (src == null || src.sharedMesh == null || src.sharedMesh.blendShapeCount == 0) continue;

                Mesh sm = src.sharedMesh, dm = dst.sharedMesh;
                var srcIdx = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < sm.blendShapeCount; i++)
                {
                    string nm = sm.GetBlendShapeName(i);
                    if (!srcIdx.ContainsKey(nm)) srcIdx[nm] = i;
                }
                var map = new int[dm.blendShapeCount];
                int matched = 0;
                for (int i = 0; i < map.Length; i++)
                {
                    int si;
                    map[i] = srcIdx.TryGetValue(dm.GetBlendShapeName(i), out si) ? si : -1;
                    if (map[i] >= 0) matched++;
                }
                if (matched > 0) pairs.Add(new MirrorPair { src = src, dst = dst, shapeMap = map });
            }
            return pairs;
        }

        // Copy VNyan-driven blendshape weights from the hidden source renderers onto the
        // visible copies every frame, after VNyan/animation has set them.
        void LateUpdate()
        {
            for (int k = 0; k < mirrorFor.Count; k++)
            {
                var pairs = mirrorFor[k];
                if (pairs == null) continue;
                for (int p = 0; p < pairs.Count; p++)
                {
                    var mp = pairs[p];
                    if (mp == null || mp.src == null || mp.dst == null) continue;
                    int[] map = mp.shapeMap;
                    for (int i = 0; i < map.Length; i++)
                    {
                        int si = map[i];
                        if (si >= 0) mp.dst.SetBlendShapeWeight(i, mp.src.GetBlendShapeWeight(si));
                    }
                }
            }
        }

        // ----------------------------------------------------------- live avatar

        // Find the avatar VNyan currently has in the scene (the one we're replacing),
        // skipping our own loaded copies. Picks the SkinnedMeshRenderer hierarchy with
        // the most skinned meshes — that's the real body, not a prop.
        GameObject FindLiveAvatar(GameObject extraExclude)
        {
            var counts = new Dictionary<Transform, int>();
            foreach (var smr in FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (smr == null || smr.sharedMesh == null) continue;
                Transform tr = smr.transform;
                if (IsUnderAnyLoaded(tr)) continue;                       // our own imports
                if (extraExclude != null && tr.IsChildOf(extraExclude.transform)) continue;
                if (window != null && tr.IsChildOf(window.transform)) continue;
                Transform root = AvatarRootOf(tr);
                if (root == null) continue;
                counts.TryGetValue(root, out int c);
                counts[root] = c + 1;
            }
            Transform best = null; int bestN = 0;
            foreach (var kv in counts)
                if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
            return best != null ? best.gameObject : null;
        }

        bool IsUnderAnyLoaded(Transform t)
        {
            foreach (var g in loaded)
                if (g != null && (t == g.transform || t.IsChildOf(g.transform))) return true;
            return false;
        }

        static Transform AvatarRootOf(Transform t)
        {
            Animator a = t.GetComponentInParent<Animator>();
            if (a != null) return a.transform;
            Transform top = t;
            while (top.parent != null) top = top.parent;
            return top;
        }

        // Replace the freshly-imported meshes' materials with the live avatar's real
        // ones (correct Standard / MToon / Poiyomi shaders, all textures + properties),
        // matched by renderer name then by submesh/material slot. The VBX can't carry
        // arbitrary third-party shaders, but the source avatar is right here in the scene.
        int BorrowMaterials(GameObject loadedGo, Transform originalRoot)
        {
            // map original renderer name -> its shared materials
            var byName = new Dictionary<string, Material[]>(StringComparer.Ordinal);
            foreach (var smr in originalRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null) continue;
                string key = smr.gameObject.name;
                if (!byName.ContainsKey(key)) byName[key] = smr.sharedMaterials;
                // also key by mesh name as a fallback alias
                if (smr.sharedMesh != null)
                {
                    string mk = smr.sharedMesh.name;
                    if (!string.IsNullOrEmpty(mk) && !byName.ContainsKey(mk)) byName[mk] = smr.sharedMaterials;
                }
            }
            foreach (var mr in originalRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null) continue;
                string key = mr.gameObject.name;
                if (!byName.ContainsKey(key)) byName[key] = mr.sharedMaterials;
            }

            int borrowed = 0;
            foreach (var smr in loadedGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Material[] src = null;
                if (!byName.TryGetValue(smr.gameObject.name, out src) &&
                    smr.sharedMesh != null) byName.TryGetValue(smr.sharedMesh.name, out src);
                if (src == null || src.Length == 0) continue;

                Material[] dst = smr.sharedMaterials;        // length = submesh count
                int n = dst != null ? dst.Length : 0;
                if (n == 0) { smr.sharedMaterials = src; borrowed += src.Length; continue; }
                for (int s = 0; s < n; s++)
                    dst[s] = src[Mathf.Min(s, src.Length - 1)];
                smr.sharedMaterials = dst;
                borrowed += n;
            }
            return borrowed;
        }

        void OpenFolder()
        {
            string dir = PrimaryDir();
            try { Application.OpenURL("file:///" + dir.Replace("\\", "/")); SetStatus("Opened: " + dir); }
            catch (Exception e) { SetStatus("Could not open folder: " + e.Message); }
        }

        // ------------------------------------------------------------ UI helpers

        void BindButton(string name, UnityEngine.Events.UnityAction action)
        {
            Button b = Find<Button>(name);
            if (b != null) b.onClick.AddListener(action);
        }

        T Find<T>(string name) where T : Component
        {
            if (window == null) return null;
            foreach (T c in window.GetComponentsInChildren<T>(true))
                if (c.gameObject.name == name) return c;
            return null;
        }

        void MakeRow(RectTransform parent, string name, string label, float y, float h, Action onClick, Func<bool> isActive)
        {
            GameObject row = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = row.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(0f, 0f); rt.offsetMax = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(0f, -y); rt.sizeDelta = new Vector2(0f, h);

            Image img = row.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.001f);
            Button btn = row.AddComponent<Button>();
            btn.targetGraphic = img;

            Text txt = MakeRowText(row.transform, label);
            Action refresh = () => {
                bool on = isActive != null && isActive();
                txt.text = (on ? "> " : "  ") + label;
                txt.color = on ? new Color(0.6f, 1f, 0.7f, 1f) : new Color(0.85f, 0.88f, 0.95f, 1f);
            };
            refresh();
            btn.onClick.AddListener(() => { if (onClick != null) onClick(); });
        }

        void MakeInfo(RectTransform parent, string text, float y, float h)
        {
            GameObject row = new GameObject("Info", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = row.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.anchoredPosition = new Vector2(0f, -y); rt.sizeDelta = new Vector2(0f, h);
            row.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            Text t = MakeRowText(row.transform, text);
            t.color = new Color(0.7f, 0.7f, 0.75f, 1f);
        }

        Text MakeRowText(Transform parent, string text)
        {
            GameObject go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(6f, 0f); rt.offsetMax = new Vector2(-2f, 0f);
            Text t = go.GetComponent<Text>();
            t.text = text; t.font = uiFont; t.fontSize = 12;
            t.alignment = TextAnchor.MiddleLeft; t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        void SetStatus(string msg) { if (statusText != null) statusText.text = msg; }
    }
}
