using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VNyanWeightStudio
{
    /// <summary>
    /// VNyan SDK plugin: "Weight Studio". A feature-rich, runtime bone-weight editor.
    ///
    ///  - Pick a skinned mesh and an active bone.
    ///  - Paint weights directly on the model with an add / subtract / replace / smooth /
    ///    blur brush (radius, strength, falloff).
    ///  - Boundary-aware Laplacian smoothing over the whole mesh or a brushed selection.
    ///  - Maintains an UNLIMITED number of bone influences per vertex.
    ///  - Export to VRM 0.x, VRM 1.0 (4-influence cap), or .vbx (unlimited influences,
    ///    re-loadable in VNyan via the companion Reader plugin).
    ///
    /// The window prefab is built by WeightStudioBuild.cs (Editor) and shipped as a
    /// .vnobj AssetBundle; this script wires it up by control name at runtime.
    /// </summary>
    public partial class BoneWeightStudioPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        public const string VERSION = "v1.1.0";
        // GitHub repo the update check reads (the check stays quiet while there are no releases)
        public const string REPO = "alienware377/VNyan-Weight-Studio";

        public GameObject windowPrefab;
        public GameObject colliderEditorPrefab;   // second window, built by WeightStudioBuild

        GameObject window;
        Text statusText;
        Text brushLabel;
        Font uiFont;

        BrushConfig brush = BrushConfig.Default;
        SmoothConfig smooth = SmoothConfig.Default;
        bool paintMode;

        readonly List<SkinnedMeshRenderer> meshes = new List<SkinnedMeshRenderer>();
        SkinnedMeshRenderer activeRenderer;
        WeightModel model;

        // heatmap overlay (Blender-style weight-paint colours)
        readonly WeightHeatmap heatmap = new WeightHeatmap();
        float heatOpacity = 0.75f;

        // weight transfer between meshes
        TransferConfig xfer = TransferConfig.Default;
        SkinnedMeshRenderer transferSource;
        Text sourceLabel;

        // auto-fit physics colliders
        ColliderGen.GenConfig genCfg = ColliderGen.GenConfig.Default();
        List<ColliderGen.GenCollider> genColliders;
        bool includeColliders = true;     // embed generated colliders in VBX/VRM exports
        Text colliderLabel;

        // collider visualization
        bool showColliders;
        Material glMat;
        Text showCollidersText;
        Transform colliderAvatarRoot;
        Dictionary<string, Transform> colliderBoneCache;

        // tappable tooltips
        Text tipText;

        RectTransform meshContent;
        RectTransform boneContent;

        // brush picking
        GameObject pickGO;
        MeshCollider pickCollider;
        Mesh pickBake;
        float lastColliderBake = -999f;
        Camera cam;

        // selection mask for "smooth selection"
        bool[] selectionMask;
        bool buildingSelection;

        float lastApply;
        bool stroking;   // true while a single LMB paint stroke is in progress (for one undo per stroke)

        public void Awake()
        {
            VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton("Weight Studio", this);
            window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(windowPrefab);
            uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            CreateGLMat();

            LoadSettings();

            if (window != null)
            {
                window.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
                window.SetActive(false);
                WireUI();
                SetStatus("Load an avatar, pick a mesh + bone, enable Paint.");
                // update badge sits straight left of the X (x 304..416 of 460); the centred
                // "Weight Studio" title glyphs end near x 280, so nothing has to move
                PluginChrome.CheckForUpdate(this, window.GetComponent<RectTransform>(), REPO, VERSION, "[WeightStudio] ", uiFont, 0f);
            }

            ColEdInit();
        }

        public void pluginButtonClicked()
        {
            if (window == null) return;
            window.SetActive(!window.activeSelf);
            if (window.activeSelf)
            {
                window.transform.SetAsLastSibling();
                RebuildMeshList();
                lastConflictCheck = -10f;   // check other plugins straight away
            }
        }

        // ------------------------------------------------------------ UI wiring

        void WireUI()
        {
            BindSliderFloat("Slider_radius", "Value_radius", brush.radius, 0.005f, 0.4f, v => brush.radius = v, "0.000");
            BindSliderFloat("Slider_strength", "Value_strength", brush.strength, 0f, 1f, v => brush.strength = v, "0.00");
            BindSliderFloat("Slider_falloff", "Value_falloff", brush.falloff, 0f, 1f, v => brush.falloff = v, "0.00");
            BindSliderInt("Slider_maxbones", "Value_maxbones", smooth.maxBonesPerVertex, 4, 16, v => smooth.maxBonesPerVertex = v);
            BindSliderInt("Slider_smoothiter", "Value_smoothiter", smooth.iterations, 1, 30, v => smooth.iterations = v);
            BindSliderFloat("Slider_smoothlambda", "Value_smoothlambda", smooth.lambda, 0f, 1f, v => smooth.lambda = v, "0.00");

            Toggle tp = Find<Toggle>("Toggle_paint");
            if (tp != null) { tp.SetIsOnWithoutNotify(paintMode); tp.onValueChanged.AddListener(v => { paintMode = v; UpdateBrushLabel(); }); }

            BindButton("Button_Add", () => SetMode(BrushMode.Add));
            BindButton("Button_Sub", () => SetMode(BrushMode.Subtract));
            BindButton("Button_Replace", () => SetMode(BrushMode.Replace));
            BindButton("Button_Smooth", () => SetMode(BrushMode.Smooth));
            BindButton("Button_Blur", () => SetMode(BrushMode.Blur));

            BindButton("Button_RescanMesh", RebuildMeshList);
            BindButton("Button_RescanBone", RebuildBoneList);
            BindButton("Button_SmoothMesh", () => DoSmooth(false));
            BindButton("Button_SmoothSel", () => DoSmooth(true));
            BindButton("Button_SmoothBone", () => DoSmoothBone(false));
            BindButton("Button_SmoothBoneSel", () => DoSmoothBone(true));
            BindButton("Button_LimitInf", DoLimit);
            BindButton("Button_Apply", () => { if (model != null) { model.ApplyToMesh(); SetStatus("Applied to mesh."); } });
            BindButton("Button_Revert", DoRevert);
            BindButton("Button_ClearSel", () => { selectionMask = null; SetStatus("Selection cleared."); });
            BindButton("Button_Undo", DoUndo);
            BindButton("Button_Redo", DoRedo);

            BindButton("Button_ExportVrm0", () => Export(ExportKind.Vrm0));
            BindButton("Button_ExportVrm1", () => Export(ExportKind.Vrm1));
            BindButton("Button_ExportVbx", () => Export(ExportKind.Vbx));
            BindButton("Button_ExportVbxFile", ExportVbxFromFile);   // standalone, from a .vsfavatar on disk

            BindButton("Button_Close", () => { if (window != null) window.SetActive(false); });
            BindButton("Button_SaveAll", () =>
            {
                SaveSettings();
                ReloadHiddenIfChanged();   // merge on top of other plugins' latest edits
                // snapshot: shown meshes leave the list; hidden ones are added ONLY if
                // the user toggled them here; a mesh hidden by a sim/proxy or another
                // plugin must not sneak into the shared list.
                foreach (var smr in meshes)
                {
                    if (smr == null) continue;
                    if (smr.enabled) hiddenMeshes.Remove(smr.name);
                    else if (wsToggled.Contains(smr.name)) hiddenMeshes.Add(smr.name);
                }
                SaveHiddenMeshes();
                SetStatus("Saved. Sliders, toggles and the current shown/hidden state persist across restarts.");
            });

            meshContent = Find<RectTransform>("MeshContent");
            boneContent = Find<RectTransform>("BoneContent");
            statusText = Find<Text>("Label_Status");
            brushLabel = Find<Text>("Label_Brush");
            tipText = Find<Text>("Label_Tip");
            sourceLabel = Find<Text>("Label_Source");

            // ---- heatmap overlay ----
            Toggle th = Find<Toggle>("Toggle_heatmap");
            if (th != null) { th.SetIsOnWithoutNotify(false); th.onValueChanged.AddListener(SetHeatmap); }
            BindSliderFloat("Slider_heatopacity", "Value_heatopacity", heatOpacity, 0f, 1f,
                v => { heatOpacity = v; heatmap.SetOpacity(v); }, "0.00");

            // ---- weight transfer ----
            BindButton("Button_SetSource", SetTransferSource);
            BindButton("Button_MapNearest", () => { xfer.mapping = TransferMapping.NearestVertex; SetStatus("Transfer mapping: Nearest vertex."); });
            BindButton("Button_MapSmooth", () => { xfer.mapping = TransferMapping.NearestSmooth; SetStatus("Transfer mapping: Smooth (averaged)."); });
            BindButton("Button_MapSurface", () => { xfer.mapping = TransferMapping.NearestSurface; SetStatus("Transfer mapping: Nearest surface."); });
            BindSliderInt("Slider_xfersamples", "Value_xfersamples", xfer.samples, 1, 16, v => xfer.samples = v);
            BindSliderFloat("Slider_xfermaxdist", "Value_xfermaxdist", xfer.maxDistance, 0f, 1f, v => xfer.maxDistance = v, "0.000");
            BindSliderFloat("Slider_xferblend", "Value_xferblend", xfer.blend, 0f, 1f, v => xfer.blend = v, "0.00");
            BindToggle("Toggle_xfermatch", xfer.matchByName, v => xfer.matchByName = v);
            BindToggle("Toggle_xfersel", xfer.selectedOnly, v => xfer.selectedOnly = v);
            BindToggle("Toggle_xfernorm", xfer.normalize, v => xfer.normalize = v);
            BindButton("Button_Transfer", DoTransfer);

            // ---- mirror weights ----
            WireMirrorUI();

            // ---- auto-fit colliders ----
            colliderLabel = Find<Text>("Label_Colliders");
            BindSliderFloat("Slider_colradius", "Value_colradius", genCfg.radiusScale, 0.05f, 2.5f, v => genCfg.radiusScale = v, "0.00");
            BindSliderFloat("Slider_colminw", "Value_colminw", genCfg.minWeight, 0.1f, 0.95f, v => genCfg.minWeight = v, "0.00");
            BindSliderInt("Slider_colminv", "Value_colminv", genCfg.minVerts, 4, 64, v => genCfg.minVerts = v);
            BindToggle("Toggle_colcaps", genCfg.capsules, v => genCfg.capsules = v);
            BindToggle("Toggle_colfingers", genCfg.skipFingers, v => genCfg.skipFingers = v);
            BindToggle("Toggle_colhuman", genCfg.humanoidOnly, v => genCfg.humanoidOnly = v);
            BindToggle("Toggle_colembed", includeColliders, v => includeColliders = v);
            BindButton("Button_GenColliders", GenerateColliders);
            BindButton("Button_ExportColliders", ExportColliderSidecars);
            BindButton("Button_ImportColliders", ImportColliders);
            BindButton("Button_ExportUnityPkg", ExportUnityPackage);
            Button scBtn = Find<Button>("Button_ShowColliders");
            if (scBtn != null) { scBtn.onClick.AddListener(OnToggleColliders); showCollidersText = scBtn.GetComponentInChildren<Text>(); }
            BindButton("Button_EditColliders", ColEdToggle);

            // ---- tappable tooltips ----
            BindAllHelp();
            BuildSections();

            // ---- window chrome: the shared X at the top-right closes the window (same as the
            // old bottom Close, which is hidden; Save Settings takes the whole bottom row) ----
            RectTransform winRt = window.GetComponent<RectTransform>();
            PluginChrome.EnsureCloseX(winRt, () => { if (window != null) window.SetActive(false); }, uiFont);
            PluginChrome.HideOtherClose(winRt, "Button_Close");

            UpdateBrushLabel();
            UpdateSourceLabel();
            UpdateColliderLabel();
            RebuildMeshList();
        }

        void SetMode(BrushMode m) { brush.mode = m; if (!paintMode) paintMode = true; UpdateBrushLabel(); }

        void UpdateBrushLabel()
        {
            if (brushLabel == null) return;
            string bone = (model != null && brush.activeBone >= 0 && brush.activeBone < model.boneNames.Length)
                ? model.boneNames[brush.activeBone] : "(no bone)";
            brushLabel.text = (paintMode ? "PAINT " : "idle ") + brush.mode + "  ->  " + bone;
        }

        // ------------------------------------------------------------ heatmap overlay

        void SetHeatmap(bool on)
        {
            heatmap.SetActiveBone(brush.activeBone);
            heatmap.SetOpacity(heatOpacity);
            heatmap.SetEnabled(on);
            SetStatus(on
                ? "Heatmap ON: blue=0, green=0.5, red=1 for the active bone. Fade with opacity."
                : "Heatmap off.");
        }

        // ------------------------------------------------------------ weight transfer

        void SetTransferSource()
        {
            if (activeRenderer == null) { SetStatus("Select a mesh first, then Set Source."); return; }
            transferSource = activeRenderer;
            UpdateSourceLabel();
            SetStatus("Transfer source = '" + transferSource.name + "'. Now select the TARGET mesh and click Transfer.");
        }

        void UpdateSourceLabel()
        {
            if (sourceLabel == null) return;
            sourceLabel.text = "Source: " + (transferSource != null ? transferSource.name : "(none: pick a mesh, Set Source)");
        }

        void DoTransfer()
        {
            if (model == null) { SetStatus("No target mesh loaded."); return; }
            if (transferSource == null) { SetStatus("No source set. Select a mesh and click Set Source."); return; }
            if (transferSource == activeRenderer) { SetStatus("Source and target are the same mesh. Pick a different target."); return; }

            model.PushUndo();
            var r = WeightTransfer.Transfer(model, transferSource, xfer, selectionMask);
            model.ApplyToMesh();
            if (heatmap.Enabled) heatmap.Refresh();
            RebuildBoneList();
            SetStatus(r.message);
        }

        // ------------------------------------------------------------ auto-fit colliders

        void UpdateColliderLabel()
        {
            if (colliderLabel == null) return;
            int n = genColliders != null ? genColliders.Count : 0;
            colliderLabel.text = n > 0
                ? "Colliders: " + n + " fitted" + (includeColliders ? " (embedded in exports)" : " (export disabled)")
                : "Colliders: none (click Generate)";
        }

        void GenerateColliders()
        {
            if (model != null && model.dirty) model.ApplyToMesh();

            Transform root;
            var renderers = GatherAvatarRenderers(out root);
            if (renderers.Count == 0) { SetStatus("No avatar meshes. Load a model and Rescan first."); return; }

            Animator anim = root != null ? root.GetComponentInChildren<Animator>() : null;

            try
            {
                var res = ColliderGen.Generate(renderers, anim, genCfg);
                ColEdPushUndo();   // a regenerate is undoable in the collider editor
                genColliders = res.colliders;
                colliderAvatarRoot = root;
                RebuildColliderBoneCache();
                UpdateColliderLabel();
                ColEdOnCollidersChanged();
                Debug.Log("[WeightStudio] GenerateColliders: renderers=" + renderers.Count
                    + " root=" + (root != null ? root.name : "null")
                    + " fitted=" + (genColliders != null ? genColliders.Count : 0));
                SetStatus(genColliders.Count > 0
                    ? "Fitted " + res.message + ". Export VBX/VRM embeds them; or use Export Colliders / Unity Package."
                    : "No colliders fitted. Lower 'Min verts' or 'Min weight', or check the mesh is readable.");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[WeightStudio] GenerateColliders failed: " + e);
                SetStatus("Generate colliders failed (see log): " + e.Message);
            }
        }

        // base file name + ensured output dir, shared by every export
        string ExportBaseName(out string dir)
        {
            dir = Path.Combine(Application.persistentDataPath, "WeightStudio");
            try { Directory.CreateDirectory(dir); } catch { }
            Transform root;
            GatherAvatarRenderers(out root);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return (root != null ? Sanitize(root.name) : "avatar") + "_" + stamp;
        }

        void ExportColliderSidecars()
        {
            if (genColliders == null || genColliders.Count == 0) { SetStatus("Generate colliders first."); return; }
            try
            {
                string dir; string baseName = ExportBaseName(out dir);
                string chosen = NativeFileDialog.SaveFile(
                    "Export Colliders: pick a base name (4 files are written next to it)",
                    dir, baseName + ".json", "Collider configs (*.json)", "*.json", "json");
                if (string.IsNullOrEmpty(chosen)) { SetStatus("Export cancelled."); return; }

                // strip .json (and a known double suffix if the user picked an existing file)
                string baseNoExt = chosen;
                if (baseNoExt.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    baseNoExt = baseNoExt.Substring(0, baseNoExt.Length - 5);
                foreach (string sfx in new[] { ".physbones", ".jigglephysics", ".vrcphysbones", ".dynamicbone" })
                    if (baseNoExt.EndsWith(sfx, StringComparison.OrdinalIgnoreCase))
                    { baseNoExt = baseNoExt.Substring(0, baseNoExt.Length - sfx.Length); break; }

                var files = CollidersExport.WriteAll(baseNoExt, genColliders);
                Debug.Log("[WeightStudio] Collider configs: " + string.Join(", ", files.ToArray()));
                SetStatus("Wrote " + files.Count + " collider configs (PhysBones/Jiggle/VRChat/DynamicBone) -> "
                    + Path.GetDirectoryName(baseNoExt));
            }
            catch (Exception e)
            {
                Debug.LogError("[WeightStudio] Collider export failed: " + e);
                SetStatus("Collider export failed (see log): " + e.Message);
            }
        }

        void ImportColliders()
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "WeightStudio");
                string chosen = NativeFileDialog.OpenFile(
                    "Import Colliders: any exported collider config",
                    Directory.Exists(dir) ? dir : Application.persistentDataPath,
                    "Collider configs (*.json)", "*.json");
                if (string.IsNullOrEmpty(chosen)) { SetStatus("Import cancelled."); return; }

                string format;
                var loaded = ColliderImport.Load(chosen, out format);
                if (loaded.Count == 0) { SetStatus("No colliders found in " + Path.GetFileName(chosen) + " (" + format + ")."); return; }

                ColEdEnsureData();
                ColEdPushUndo();

                // merge: same name replaces, new names append
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < genColliders.Count; i++)
                    if (!string.IsNullOrEmpty(genColliders[i].name)) index[genColliders[i].name] = i;

                int added = 0, replaced = 0;
                foreach (var c in loaded)
                {
                    int at;
                    if (!string.IsNullOrEmpty(c.name) && index.TryGetValue(c.name, out at))
                    { genColliders[at] = c; replaced++; }
                    else
                    {
                        genColliders.Add(c);
                        if (!string.IsNullOrEmpty(c.name)) index[c.name] = genColliders.Count - 1;
                        added++;
                    }
                }

                UpdateColliderLabel();
                ColEdOnCollidersChanged();
                if (!showColliders) OnToggleColliders();   // show what just came in
                SetStatus("Imported " + added + " new collider(s)" +
                    (replaced > 0 ? " (" + replaced + " replaced)" : "") +
                    " from " + Path.GetFileName(chosen) + " [" + format + "]");
            }
            catch (Exception e)
            {
                Debug.LogError("[WeightStudio] Collider import failed: " + e);
                SetStatus("Collider import failed (see log): " + e.Message);
            }
        }

        void ExportUnityPackage()
        {
            if (model != null && model.dirty) model.ApplyToMesh();

            // gather the CHOSEN avatar's meshes (same scoping as Export) so we embed
            // exactly one copy of the model, not other avatars / a Pose Studio copy.
            Transform root;
            var renderers = GatherAvatarRenderers(out root);

            string tempVbx = null;
            try
            {
                string dir; string baseName = ExportBaseName(out dir);
                string chosen = NativeFileDialog.SaveFile("Export Unity Package", dir, baseName + ".unitypackage",
                    "Unity package (*.unitypackage)", "*.unitypackage", "unitypackage");
                if (string.IsNullOrEmpty(chosen)) { SetStatus("Export cancelled."); return; }
                try { Directory.CreateDirectory(Path.GetDirectoryName(chosen)); } catch { }
                string path = chosen;

                // Build the model VBX into a temp file and embed it in the package, alongside
                // a ScriptedImporter that re-imports it with UNLIMITED skin weights (>4/vertex).
                string vbxAssetName = null;
                if (renderers != null && renderers.Count > 0)
                {
                    Animator anim = root != null ? root.GetComponentInChildren<Animator>() : null;
                    var cols = (includeColliders && genColliders != null && genColliders.Count > 0) ? genColliders : null;
                    vbxAssetName = (root != null ? Sanitize(root.name) : "avatar") + ".vbx";
                    tempVbx = Path.Combine(Path.GetTempPath(), "WeightStudio_" + Guid.NewGuid().ToString("N") + ".vbx");
                    VbxExporter.Export(tempVbx, root, renderers, anim, cols);
                }

                UnityPackageExport.Export(path, genColliders, tempVbx, vbxAssetName);
                Debug.Log("[WeightStudio] Unity package: " + path);
                SetStatus(vbxAssetName != null
                    ? "Exported Unity package (model + .vbx importer, unlimited skin weights) -> " + path
                    : "Exported Unity addon package (scripts only, no avatar selected) -> " + path);
            }
            catch (Exception e)
            {
                Debug.LogError("[WeightStudio] Unity package export failed: " + e);
                SetStatus("Unity package export failed (see log): " + e.Message);
            }
            finally
            {
                if (tempVbx != null) { try { File.Delete(tempVbx); } catch { } }
            }
        }

        // ------------------------------------------------------------ tooltips

        void BindAllHelp()
        {
            var tips = Tips();
            foreach (var kv in tips)
            {
                string text = kv.Value;
                Button b = Find<Button>("Help_" + kv.Key);
                if (b != null) b.onClick.AddListener(() => SetTip(text));
            }
            SetTip("Tap any  ?  for a plain-language explanation of that option.");
        }

        void SetTip(string msg) { if (tipText != null) tipText.text = msg; }

        static Dictionary<string, string> Tips()
        {
            return new Dictionary<string, string>
            {
                ["radius"] = "Brush size in metres. Bigger = paints more of the model under one stroke.",
                ["strength"] = "How hard each stroke pushes the weight. Higher makes bigger changes per pass.",
                ["falloff"] = "Brush edge softness. 0 = hard edge (full strength to the rim); 1 = soft, fading out.",
                ["maxbones"] = "Most bones a single point may attach to. Lower = simpler/cheaper; used by Limit and Smooth.",
                ["smoothiter"] = "How many smoothing passes to run. More = smoother, but slower and can wash out detail.",
                ["smoothlambda"] = "How far each smoothing pass blends toward neighbours. 0 = none, 1 = fully averaged.",
                ["paint"] = "Turn on, then hold the left mouse button and drag on the model to paint. Hold Shift to mark a selection instead.",
                ["brushmodes"] = "Add raises the active bone's pull; Sub lowers it; Replace pushes toward a value; Smooth evens it with neighbours; Blur softens all bones nearby.",
                ["heatmap"] = "Colours the model by the active bone's weight (blue=0, green=0.5, red=1), like Blender's weight-paint view. Updates live as you paint.",
                ["heatopacity"] = "How solid the colour overlay is. Lower it to see the model's real textures underneath.",
                ["meshes"] = "Skinned meshes on your avatar. Click a name to edit it; click 'shown/hidden' on the right to hide a mesh that blocks your view while painting. Press Rescan after loading a new model.",
                ["undo"] = "Step back through your edits (brush strokes, Smooth, Limit, Transfer). Also Ctrl+Z. Keeps the last 24 steps per mesh.",
                ["redo"] = "Re-apply an edit you just undid. Also Ctrl+Shift+Z.",
                ["bones"] = "Bones of the selected mesh. Click one to make it the active bone your brush (and the heatmap) target.",
                ["smoothmesh"] = "Smooths choppy weight edges across the whole mesh so bends look natural.",
                ["smoothsel"] = "Same smoothing, but only on the area you Shift-painted as a selection.",
                ["smoothbone"] = "Smooths ONLY the active bone's weights across the whole mesh, leaving other bones' weights' relative balance intact.",
                ["smoothbonesel"] = "Smooths only the active bone's weights, and only inside the area you Shift-painted as a selection.",
                ["limitinf"] = "Caps every point to the 'Max bones / vert' count, keeping the strongest. Good before VRM export.",
                ["apply"] = "Writes your current edits onto the live mesh so you see them on the model.",
                ["revert"] = "Discards unsaved edits and returns to the last applied state.",
                ["clearsel"] = "Clears the Shift-painted selection.",
                ["export"] = "Save the avatar. VRM 0.x / 1.0 cap at 4 bones per point (standard); VBX keeps unlimited bones and reloads with the Reader plugin.",
                ["xfersource"] = "COPY-FROM mesh. Select it in the list, click Set Source. Then select the COPY-INTO mesh and click Transfer.",
                ["xfermap"] = "How weights are sampled: Nearest = closest point's weights; Smooth = average of several nearby points; Surface = blend from the closest triangle (most accurate).",
                ["xfersamples"] = "Smooth mapping only: how many nearby source points to average. More = smoother, less detail.",
                ["xfermaxdist"] = "Ignore source points farther than this (metres). 0 = no limit. Stops weights leaking from the wrong body part.",
                ["xferblend"] = "How much copied weight to apply. 1 = fully replace the target; 0.5 = mix half-and-half with what's there.",
                ["xfermatch"] = "Match bones by NAME between the two models (recommended). Off = match by bone order, only safe for identical skeletons.",
                ["xfersel"] = "Only change the Shift-painted selection instead of the whole mesh.",
                ["xfernorm"] = "Make each point's weights total 1 after transfer (recommended; prevents distortion).",
                ["xfergo"] = "Copy weights from the Source mesh onto the selected (target) mesh using the settings above.",
                ["mirror"] = "Copy weights across the mirror axis. Bone -> Opposite copies the active bone's weights onto its L/R partner. Self copies one side of the active bone onto its own other side.",
                ["mirproj"] = "OFF: match exact mirrored verts using the model's default (bind) pose. ON: project onto the CURRENT pose. For projection, pose must be symmetric, held still, nothing clipping together, no live tracking.",
                ["mirmix"] = "How much of the mirrored value to take. 0% = keep what's there, 100% = fully replace the target side/bone.",
                ["colgen"] = "Auto-fit a collider to each bone, hugging the parts of the mesh that bone controls. Capsules for long bones, spheres for round ones.",
                ["colradius"] = "Scales every fitted collider's thickness. 1 = snug to the mesh; higher = puffier; lower = tighter inside.",
                ["colminw"] = "A vertex counts toward a bone only if that bone is at least this much of its weight. Higher = tighter, cleaner fits.",
                ["colminv"] = "Skip bones controlling fewer vertices than this. Raises to drop tiny noisy colliders; lower to cover small parts.",
                ["colcaps"] = "Allow capsule (pill) shapes for long bones like arms and legs. Off = spheres only.",
                ["colfingers"] = "Skip the small finger and toe bones. They rarely need colliders and just add clutter.",
                ["colhuman"] = "Only fit colliders on standard humanoid bones (needs a humanoid rig). Off = every skinning bone.",
                ["colembed"] = "Bake the fitted colliders into the VBX / VRM files you export, so spring-bones collide with the body.",
                ["colsidecar"] = "Save the colliders as JSON for PhysBones, Jiggle, VRChat PhysBones and DynamicBone. You pick where; 4 files are written.",
                ["colimport"] = "Load colliders back from ANY exported collider config (PhysBones / Jiggle / VRChat / DynamicBone JSON). Same names get replaced.",
                ["colunitypkg"] = "Export a .unitypackage with Unity 2019.4 / 2022.3 scripts (collider + spring-bone + importer) plus a ready-made collider config.",
            };
        }

        // ------------------------------------------------------------ paint loop

        float lastHiddenApply = -999f;

        // Re-hide saved hidden meshes even when our window is closed, so the state
        // survives restarts and avatar reloads without opening Weight Studio.
        void ApplyHiddenMeshes()
        {
            if (!hiddenLoaded) LoadHiddenMeshes();
            else ReloadHiddenIfChanged();
            if (hiddenMeshes.Count == 0) return;
            foreach (var smr in FindObjectsOfType<SkinnedMeshRenderer>())
                if (smr != null && smr.enabled && hiddenMeshes.Contains(smr.name))
                    smr.enabled = false;
        }

        void Update()
        {
            if (Time.unscaledTime - lastHiddenApply > 2f)
            {
                lastHiddenApply = Time.unscaledTime;
                ApplyHiddenMeshes();
            }
            ShapeCaptureTick();     // end-of-frame shape keys (VNyan zeroes them mid-frame)
            CheckConflictsTick();   // soft-body / weight plugins changing the avatar under us
            ProbeTick();            // developer self-test, only when its trigger file exists

            if (ColEdUpdate()) return;   // 3D-view collider picking eats the click

            if (window == null || !window.activeSelf) return;
            if (!paintMode || model == null || !model.Loaded) return;
            if (!Input.GetMouseButton(0)) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

            EnsureCamera();
            if (cam == null) return;

            // (re)bake the pick surface periodically
            if (Time.unscaledTime - lastColliderBake > 0.2f) RebuildPickSurface();

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (pickCollider == null) return;
            RaycastHit hit;
            if (!pickCollider.Raycast(ray, out hit, 1000f)) return;

            model.RefreshWorldPositions();

            if (buildingSelection)
                PaintSelection(hit.point);
            else
            {
                if (!stroking) { model.PushUndo(); stroking = true; }  // one undo step per stroke
                WeightTools.ApplyBrush(model, hit.point, brush);
                if (Time.unscaledTime - lastApply > 0.05f)
                {
                    model.ApplyToMesh();
                    if (heatmap.Enabled) heatmap.Refresh();
                    lastApply = Time.unscaledTime;
                }
            }
        }

        void EnsureCamera()
        {
            if (cam != null) return;
            cam = Camera.main;
            if (cam == null)
            {
                var all = Camera.allCameras;
                if (all.Length > 0) cam = all[0];
            }
        }

        void RebuildPickSurface()
        {
            if (activeRenderer == null) return;
            if (pickGO == null)
            {
                pickGO = new GameObject("WeightStudio_PickSurface");
                pickCollider = pickGO.AddComponent<MeshCollider>();
            }
            if (pickBake == null) pickBake = new Mesh();
            LiveShapes.Bake(activeRenderer, pickBake);   // the surface you see, shape keys included
            pickGO.transform.SetParent(activeRenderer.transform, false);
            pickGO.transform.localPosition = Vector3.zero;
            pickGO.transform.localRotation = Quaternion.identity;
            pickGO.transform.localScale = Vector3.one;
            pickCollider.sharedMesh = null;
            pickCollider.sharedMesh = pickBake;
            lastColliderBake = Time.unscaledTime;
        }

        // ------------------------------------------------------------ selection painting

        void PaintSelection(Vector3 worldHit)
        {
            if (model == null || model.weldWorldPos == null) return;
            if (selectionMask == null || selectionMask.Length != model.weldCount)
                selectionMask = new bool[model.weldCount];
            float r2 = brush.radius * brush.radius;
            int added = 0;
            for (int w = 0; w < model.weldCount; w++)
            {
                if (selectionMask[w]) continue;
                if ((model.weldWorldPos[w] - worldHit).sqrMagnitude <= r2) { selectionMask[w] = true; added++; }
            }
            if (added > 0) SetStatus("Selecting... " + CountSelection() + " verts.");
        }

        int CountSelection()
        {
            if (selectionMask == null) return 0;
            int n = 0; for (int i = 0; i < selectionMask.Length; i++) if (selectionMask[i]) n++; return n;
        }

        // ------------------------------------------------------------ mesh / bone lists

        // meshes hidden via OUR shown/hidden buttons, persisted across sessions. Only
        // names in this set are ever re-hidden; meshes other plugins disable are ignored.
        readonly HashSet<string> hiddenMeshes = new HashSet<string>();
        readonly HashSet<string> wsToggled = new HashSet<string>();   // meshes the user clicked in OUR list this session
        bool hiddenLoaded;

        // Root AppData so every plugin can share it even when installed alone.
        // Migrates the old WeightStudio\hidden_meshes.txt copy once if present.
        string HiddenMeshPath()
        {
            string p = Path.Combine(Application.persistentDataPath, "hidden_meshes.txt");
            try
            {
                string old = Path.Combine(Path.Combine(Application.persistentDataPath, "WeightStudio"), "hidden_meshes.txt");
                if (!File.Exists(p) && File.Exists(old)) File.Copy(old, p);
            }
            catch { }
            return p;
        }

        // The file is SHARED with the soft-body studios (Jello etc.): they read and
        // write the same list, so track its timestamp and reload when someone else
        // changes it, or our stale copy would clobber their edits on the next save.
        DateTime hiddenStamp;

        void LoadHiddenMeshes()
        {
            hiddenLoaded = true;
            try
            {
                string p = HiddenMeshPath();
                hiddenMeshes.Clear();
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p))
                    if (!string.IsNullOrEmpty(line.Trim())) hiddenMeshes.Add(line.Trim());
                hiddenStamp = File.GetLastWriteTimeUtc(p);
            }
            catch (Exception e) { Debug.LogWarning("[WeightStudio] hidden-mesh load failed: " + e.Message); }
        }

        void SaveHiddenMeshes()
        {
            try
            {
                string[] arr = new string[hiddenMeshes.Count];
                hiddenMeshes.CopyTo(arr);
                File.WriteAllLines(HiddenMeshPath(), arr);
                hiddenStamp = File.GetLastWriteTimeUtc(HiddenMeshPath());
            }
            catch (Exception e) { Debug.LogWarning("[WeightStudio] hidden-mesh save failed: " + e.Message); }
        }

        void ReloadHiddenIfChanged()
        {
            try
            {
                string p = HiddenMeshPath();
                if (File.Exists(p) && File.GetLastWriteTimeUtc(p) != hiddenStamp) LoadHiddenMeshes();
            }
            catch { }
        }

        void RebuildMeshList()
        {
            if (!hiddenLoaded) LoadHiddenMeshes();
            meshes.Clear();
            var renderers = FindObjectsOfType<SkinnedMeshRenderer>();
            Array.Sort(renderers, (a, b) => string.Compare(a ? a.name : "", b ? b.name : "", StringComparison.OrdinalIgnoreCase));
            foreach (var smr in renderers)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                if (smr.bones == null || smr.bones.Length < 2) continue;
                if (hiddenMeshes.Contains(smr.name) && smr.enabled) smr.enabled = false;   // restore saved hide state
                meshes.Add(smr);
            }

            if (meshContent == null) return;
            for (int i = meshContent.childCount - 1; i >= 0; i--) Destroy(meshContent.GetChild(i).gameObject);

            const float rowH = 22f;
            float y = 0f;
            foreach (var smr in meshes)
            {
                SkinnedMeshRenderer captured = smr;
                GameObject row = MakeRow(meshContent, "Mesh_" + smr.name, smr.name, y, rowH,
                    () => SelectMesh(captured),
                    () => activeRenderer == captured);
                AddVisibilityButton(row, captured);
                y += rowH;
            }
            if (meshes.Count == 0) MakeInfo(meshContent, "(no skinned meshes: load an avatar, then Rescan)", y, rowH);
            meshContent.sizeDelta = new Vector2(0f, Mathf.Max(y, 1f));

            if (activeRenderer == null && meshes.Count > 0) SelectMesh(meshes[0]);
        }

        void SelectMesh(SkinnedMeshRenderer smr)
        {
            if (smr == null) return;
            if (model != null && model.dirty) model.ApplyToMesh();

            activeRenderer = smr;
            model = WeightModel.Load(smr);
            selectionMask = null;
            if (model == null) { SetStatus("Could not load '" + smr.name + "' (mesh not readable?)."); return; }
            brush.activeBone = Mathf.Clamp(brush.activeBone, 0, Mathf.Max(0, model.boneNames.Length - 1));
            heatmap.Attach(activeRenderer, model);   // re-bind overlay to the new working mesh
            heatmap.SetActiveBone(brush.activeBone);
            RebuildPickSurface();
            RebuildBoneList();
            RebuildMeshList();
            UpdateBrushLabel();
            SetStatus("Editing '" + smr.name + "': " + model.vertexCount + " verts, up to " + model.MaxBonesPerVertex() + " bones/vertex.");
        }

        void RebuildBoneList()
        {
            if (boneContent == null) return;
            for (int i = boneContent.childCount - 1; i >= 0; i--) Destroy(boneContent.GetChild(i).gameObject);
            if (model == null) { MakeInfo(boneContent, "(select a mesh first)", 0f, 22f); boneContent.sizeDelta = new Vector2(0, 22f); return; }

            const float rowH = 20f;
            float y = 0f;
            for (int i = 0; i < model.boneNames.Length; i++)
            {
                int bi = i;
                MakeRow(boneContent, "Bone_" + i, model.boneNames[i], y, rowH,
                    () => { brush.activeBone = bi; heatmap.SetActiveBone(bi); UpdateBrushLabel(); RebuildBoneList(); },
                    () => brush.activeBone == bi);
                y += rowH;
            }
            boneContent.sizeDelta = new Vector2(0f, Mathf.Max(y, 1f));
        }

        // ------------------------------------------------------------ tools

        void DoSmooth(bool selectionOnly)
        {
            if (model == null) { SetStatus("No mesh loaded."); return; }
            bool[] mask = selectionOnly ? selectionMask : null;
            if (selectionOnly && (mask == null || CountSelection() == 0)) { SetStatus("No selection. Paint one with 'Select' first."); return; }
            model.PushUndo();
            var r = WeightTools.SmoothMesh(model, smooth, mask);
            model.ApplyToMesh();
            if (heatmap.Enabled) heatmap.Refresh();
            SetStatus("Smoothed " + r.affectedWelds + " seed verts, up to " + r.maxBonesObserved + " bones/vertex.");
        }

        void DoSmoothBone(bool selectionOnly)
        {
            if (model == null) { SetStatus("No mesh loaded."); return; }
            if (brush.activeBone < 0) { SetStatus("No active bone selected."); return; }
            bool[] mask = selectionOnly ? selectionMask : null;
            if (selectionOnly && (mask == null || CountSelection() == 0)) { SetStatus("No selection. Paint one with 'Select' first."); return; }
            model.PushUndo();
            var r = WeightTools.SmoothBone(model, brush.activeBone, smooth, mask);
            model.ApplyToMesh();
            if (heatmap.Enabled) heatmap.Refresh();
            string bn = (model.boneNames != null && brush.activeBone < model.boneNames.Length)
                ? model.boneNames[brush.activeBone] : ("bone " + brush.activeBone);
            SetStatus("Smoothed '" + bn + "' over " + r.affectedWelds + " verts.");
        }

        void DoLimit()
        {
            if (model == null) { SetStatus("No mesh loaded."); return; }
            model.PushUndo();
            WeightTools.LimitInfluences(model, smooth.maxBonesPerVertex);
            model.ApplyToMesh();
            if (heatmap.Enabled) heatmap.Refresh();
            SetStatus("Limited to " + smooth.maxBonesPerVertex + " influences/vertex.");
        }

        void DoRevert()
        {
            if (activeRenderer == null) return;
            model = WeightModel.Load(activeRenderer);
            selectionMask = null;
            heatmap.Attach(activeRenderer, model);
            heatmap.SetActiveBone(brush.activeBone);
            RebuildBoneList();
            SetStatus("Reverted to last applied mesh state.");
        }

        void DoUndo()
        {
            if (model == null) { SetStatus("No mesh loaded."); return; }
            if (!model.Undo()) { SetStatus("Nothing to undo."); return; }
            model.ApplyToMesh();
            if (heatmap.Enabled) heatmap.Refresh();
            RebuildBoneList();
            SetStatus("Undo (" + model.UndoDepth + " more available, " + model.RedoDepth + " to redo).");
        }

        void DoRedo()
        {
            if (model == null) { SetStatus("No mesh loaded."); return; }
            if (!model.Redo()) { SetStatus("Nothing to redo."); return; }
            model.ApplyToMesh();
            if (heatmap.Enabled) heatmap.Refresh();
            RebuildBoneList();
            SetStatus("Redo (" + model.RedoDepth + " more available).");
        }

        // ------------------------------------------------------------ export

        enum ExportKind { Vrm0, Vrm1, Vbx }

        void Export(ExportKind kind)
        {
            if (model != null && model.dirty) model.ApplyToMesh();

            // gather all meshes of the CHOSEN avatar (not every SMR in the scene: other
            // avatars / a Pose Studio copy / our heatmap overlay would otherwise duplicate).
            Transform root;
            var renderers = GatherAvatarRenderers(out root);
            if (renderers.Count == 0) { SetStatus("No avatar meshes to export. Select a mesh first."); return; }

            Animator anim = root != null ? root.GetComponentInChildren<Animator>() : null;

            string dir = Path.Combine(Application.persistentDataPath, "WeightStudio");
            try { Directory.CreateDirectory(dir); } catch { }
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string baseName = (root != null ? Sanitize(root.name) : "avatar") + "_" + stamp;

            var cols = (includeColliders && genColliders != null && genColliders.Count > 0) ? genColliders : null;

            // let the user pick the file name + location (defaults to the WeightStudio folder)
            string defName, defExt, filterLabel, filterPattern, dlgTitle;
            switch (kind)
            {
                case ExportKind.Vbx:
                    defName = baseName + ".vbx"; defExt = "vbx";
                    filterLabel = "Weight Studio VBX (*.vbx)"; filterPattern = "*.vbx"; dlgTitle = "Export VBX"; break;
                case ExportKind.Vrm0:
                    defName = baseName + ".vrm"; defExt = "vrm";
                    filterLabel = "VRM 0.x (*.vrm)"; filterPattern = "*.vrm"; dlgTitle = "Export VRM 0.x"; break;
                default:
                    defName = baseName + "_vrm1.vrm"; defExt = "vrm";
                    filterLabel = "VRM 1.0 (*.vrm)"; filterPattern = "*.vrm"; dlgTitle = "Export VRM 1.0"; break;
            }
            string chosen = NativeFileDialog.SaveFile(dlgTitle, dir, defName, filterLabel, filterPattern, defExt);
            if (string.IsNullOrEmpty(chosen)) { SetStatus("Export cancelled."); return; }
            try { Directory.CreateDirectory(Path.GetDirectoryName(chosen)); } catch { }

            try
            {
                string path = chosen;
                switch (kind)
                {
                    case ExportKind.Vbx:
                        VbxExporter.Export(path, root, renderers, anim, cols);
                        break;
                    case ExportKind.Vrm0:
                        GltfVrmExporter.Export(path, GltfVrmExporter.VrmVersion.Vrm0, root, renderers, anim, MetaFor(root), cols);
                        break;
                    default:
                        GltfVrmExporter.Export(path, GltfVrmExporter.VrmVersion.Vrm1, root, renderers, anim, MetaFor(root), cols);
                        break;
                }
                string colNote = cols != null ? "  (+" + cols.Count + " colliders)" : "";
                Debug.Log("[WeightStudio] Exported: " + path + colNote);
                if (kind == ExportKind.Vbx)
                    SetStatus("Exported -> " + path + colNote +
                              "  |  This .vbx is an OVERLAY: load it in the Reader WITH this avatar open so it rides the tracked rig and borrows the real shaders. " +
                              "For a portable standalone with baked-in shaders, use \"Export VBX from .vsfavatar file\".");
                else
                    SetStatus("Exported -> " + path + colNote);
            }
            catch (Exception e)
            {
                Debug.LogError("[WeightStudio] Export failed: " + e);
                SetStatus("Export failed (see log): " + e.Message);
            }
        }

        // ------------------------------------------------ standalone export from a .vsfavatar
        // Reads an avatar straight out of a .vsfavatar file (a Unity AssetBundle) and
        // writes a fully self-contained .vbx, no need for the avatar to be loaded in
        // VNyan, and nothing is left in the live scene afterwards. Triggered by the
        // "Button_ExportVbxFile" UI button if present, or Ctrl+Shift+E while the window
        // is open.
        // Optional hand-off to the separate Material Studio plugin (no reference to it: found
        // by name at runtime). Returns how many materials it changed, or -1 when it is not
        // installed or has no saved edits for this model.
        static int ApplyMaterialStudioEdits(GameObject avatar, string sourceFile)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType("MaterialStudio.MaterialStudioPlugin", false);
                    if (t == null) continue;
                    var mi = t.GetMethod("ApplyRecipeTo", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (mi == null) return -1;
                    return (int)mi.Invoke(null, new object[] { avatar, sourceFile });
                }
            }
            catch (Exception e) { Debug.LogWarning("[WeightStudio] Material Studio edits not applied: " + e.Message); }
            return -1;
        }

        void ExportVbxFromFile()
        {
            string startDir = Path.Combine(Application.persistentDataPath, "WeightStudio");
            try { Directory.CreateDirectory(startDir); } catch { }

            string vsf = NativeFileDialog.OpenFile(
                "Select a .vsfavatar to convert to a standalone VBX",
                startDir, "VSeeFace / VNyan Avatar (*.vsfavatar)", "*.vsfavatar");
            if (string.IsNullOrEmpty(vsf)) { SetStatus("Standalone export cancelled."); return; }

            SetStatus("Reading .vsfavatar (this can take a moment for large avatars)...");

            VsfAvatarReader.Loaded loaded = null;
            try
            {
                loaded = VsfAvatarReader.Load(vsf);
                if (loaded == null || loaded.instance == null)
                {
                    SetStatus("Couldn't find an avatar inside that .vsfavatar (see log).");
                    return;
                }

                Transform root = loaded.instance.transform;
                var renderers = new List<SkinnedMeshRenderer>();
                foreach (var smr in loaded.instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (smr != null && smr.sharedMesh != null) renderers.Add(smr);
                if (renderers.Count == 0)
                {
                    SetStatus("That avatar has no skinned meshes to export.");
                    return;
                }

                Animator anim = loaded.instance.GetComponentInChildren<Animator>(true);

                string baseName = Sanitize(string.IsNullOrEmpty(loaded.sourceName)
                    ? Path.GetFileNameWithoutExtension(vsf) : loaded.sourceName);
                string outDir = Path.GetDirectoryName(vsf);
                string chosen = NativeFileDialog.SaveFile(
                    "Save standalone VBX", outDir, baseName + ".vbx",
                    "Weight Studio VBX (*.vbx)", "*.vbx", "vbx");
                if (string.IsNullOrEmpty(chosen)) { SetStatus("Standalone export cancelled."); return; }
                try { Directory.CreateDirectory(Path.GetDirectoryName(chosen)); } catch { }

                // Build reads every vertex into managed memory and bakes textures to PNG,
                // so all data is captured before Cleanup unloads the bundle. Colliders are
                // not passed here (they were fitted for the live avatar, not this file).
                // Embed the source .vsfavatar bytes so the Reader can recover the REAL
                // compiled materials (locked Poiyomi, etc.) on a standalone load.
                // Live material edits from Material Studio (when it is installed) go onto this
                // copy too, so the standalone .vbx matches what VNyan shows.
                int msApplied = ApplyMaterialStudioEdits(loaded.instance, vsf);
                if (msApplied > 0) Debug.Log("[WeightStudio] Material Studio edits applied to " + msApplied + " material(s).");

                VbxExporter.Export(chosen, root, renderers, anim, null, vsf);

                int unreadable = 0;
                foreach (var smr in renderers)
                    if (smr.sharedMesh != null && !smr.sharedMesh.isReadable) unreadable++;
                string note = unreadable > 0
                    ? "  (WARNING: " + unreadable + " mesh(es) had Read/Write disabled and were skipped)"
                    : "";

                Debug.Log("[WeightStudio] Standalone VBX exported from " + vsf + " -> " + chosen + note);
                SetStatus("Standalone VBX -> " + chosen + note);
            }
            catch (Exception e)
            {
                Debug.LogError("[WeightStudio] Standalone export failed: " + e);
                SetStatus("Standalone export failed (see log): " + e.Message);
            }
            finally
            {
                VsfAvatarReader.Cleanup(loaded);
            }
        }

        // ---------------------------------------------------------- TEMPORARY recovery tool
        // Reads a (possibly bug-duplicated) .vbx and re-applies its weights onto the
        // CURRENTLY-LOADED original avatar, matching meshes by name and bones by name.
        // Duplicate copies of a mesh are de-duplicated: for each renderer we pick the VBX
        // copy whose influences best resolve onto the loaded skeleton (the stray second
        // avatar's copy has an all -1 boneMap, so it scores ~0 and is ignored).
        // Triggered by Ctrl+Shift+R while the window is open. Remove after recovery.
        void RecoverWeightsFromVbx()
        {
            string dir = Path.Combine(Application.persistentDataPath, "WeightStudio");
            try { Directory.CreateDirectory(dir); } catch { }
            string path = NativeFileDialog.OpenFile(
                "Recover weights from VBX (ignores duplicate meshes)",
                dir, "Weight Studio VBX (*.vbx)", "*.vbx");
            if (string.IsNullOrEmpty(path)) { SetStatus("Weight recovery cancelled."); return; }

            VbxDoc doc;
            try { using (var fs = File.OpenRead(path)) doc = VbxIO.Read(fs); }
            catch (Exception e)
            {
                Debug.LogError("[WeightStudio] VBX read failed: " + e);
                SetStatus("Couldn't read VBX (see log): " + e.Message);
                return;
            }

            Transform root;
            var targets = GatherAvatarRenderers(out root);
            if (targets.Count == 0) { SetStatus("Load the original avatar first, then press Ctrl+Shift+R."); return; }

            int done = 0, skipped = 0, dupIgnored = 0;
            var sb = new StringBuilder();

            foreach (var smr in targets)
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null) { skipped++; continue; }
                int vc = mesh.vertexCount;

                // candidate VBX meshes: same renderer name AND same vertex count (same topology)
                var candidates = new List<VbxMesh>();
                foreach (var vm in doc.meshes)
                    if (vm.name == smr.name && vm.positions != null && vm.positions.Length == vc)
                        candidates.Add(vm);

                if (candidates.Count == 0)
                {
                    skipped++;
                    sb.Append("  - ").Append(smr.name).Append(": no matching mesh in VBX\n");
                    continue;
                }
                if (candidates.Count > 1) dupIgnored += candidates.Count - 1;

                // name -> bone slot on the LOADED renderer
                Transform[] bones = smr.bones;
                var nameToSlot = new Dictionary<string, int>(bones != null ? bones.Length : 0);
                if (bones != null)
                    for (int i = 0; i < bones.Length; i++)
                        if (bones[i] != null && !nameToSlot.ContainsKey(bones[i].name))
                            nameToSlot[bones[i].name] = i;

                // pick the copy whose influences resolve best onto the loaded skeleton
                VbxMesh chosen = null;
                int[] chosenMap = null;
                double bestScore = -1.0;
                float chosenAvg = 0f;
                foreach (var cand in candidates)
                {
                    int[] l2t = BuildLocalToTarget(cand, doc, nameToSlot);
                    long mapped = 0, total = 0; int off = 0;
                    int n2 = cand.bonesPerVertex != null ? cand.bonesPerVertex.Length : 0;
                    for (int v = 0; v < n2; v++)
                    {
                        int n = cand.bonesPerVertex[v];
                        for (int k = 0; k < n; k++)
                        {
                            int idx = off + k;
                            if (idx >= cand.influenceWeight.Length) break;
                            if (cand.influenceWeight[idx] <= 0f) continue;
                            total++;
                            int ls = cand.influenceBone[idx];
                            if (ls >= 0 && ls < l2t.Length && l2t[ls] >= 0) mapped++;
                        }
                        off += n;
                    }
                    double score = total > 0 ? (double)mapped / total : 0.0;
                    if (score > bestScore)
                    {
                        bestScore = score; chosen = cand; chosenMap = l2t; chosenAvg = AvgInfluences(cand);
                    }
                }

                if (chosen == null || bestScore <= 0.0)
                {
                    skipped++;
                    sb.Append("  - ").Append(smr.name).Append(": bones don't match this skeleton (skipped)\n");
                    continue;
                }

                // decode + write the chosen copy's weights, remapped to this renderer's bone slots
                var outBpv = new NativeArray<byte>(vc, Allocator.Temp);
                var flatOut = new List<BoneWeight1>(vc * 4);
                var tmp = new List<KeyValuePair<int, float>>();
                int offset = 0;
                long droppedInf = 0;
                int bpvLen = chosen.bonesPerVertex != null ? chosen.bonesPerVertex.Length : 0;
                for (int v = 0; v < vc; v++)
                {
                    int n = v < bpvLen ? chosen.bonesPerVertex[v] : 0;
                    var acc = new Dictionary<int, float>();
                    for (int k = 0; k < n; k++)
                    {
                        int idx = offset + k;
                        if (idx >= chosen.influenceWeight.Length) break;
                        float wgt = chosen.influenceWeight[idx];
                        int ls = chosen.influenceBone[idx];
                        int ts = (ls >= 0 && ls < chosenMap.Length) ? chosenMap[ls] : -1;
                        if (ts < 0 || wgt <= 0f) { if (wgt > 0f) droppedInf++; continue; }
                        acc[ts] = acc.TryGetValue(ts, out float ex) ? ex + wgt : wgt;
                    }
                    offset += n;

                    tmp.Clear();
                    foreach (var kv in acc) tmp.Add(kv);
                    tmp.Sort((x, y) => y.Value.CompareTo(x.Value)); // descending, required by SetBoneWeights
                    if (tmp.Count == 0)
                    {
                        outBpv[v] = 1;
                        flatOut.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
                        continue;
                    }
                    float sum = 0f; for (int k = 0; k < tmp.Count; k++) sum += tmp[k].Value;
                    float inv = sum > 0f ? 1f / sum : 0f;
                    outBpv[v] = (byte)Mathf.Min(tmp.Count, 255);
                    for (int k = 0; k < tmp.Count; k++)
                        flatOut.Add(new BoneWeight1 { boneIndex = tmp[k].Key, weight = tmp[k].Value * inv });
                }

                var outW = new NativeArray<BoneWeight1>(flatOut.Count, Allocator.Temp);
                for (int i = 0; i < flatOut.Count; i++) outW[i] = flatOut[i];
                mesh.SetBoneWeights(outBpv, outW);
                outBpv.Dispose(); outW.Dispose();
                if (QualitySettings.skinWeights != SkinWeights.Unlimited)
                    QualitySettings.skinWeights = SkinWeights.Unlimited;
                smr.quality = SkinQuality.Auto;

                done++;
                sb.Append("  - ").Append(smr.name).Append(": recovered (")
                  .Append(candidates.Count).Append(" copy(ies), match ")
                  .Append((bestScore * 100.0).ToString("0")).Append("%, avg ")
                  .Append(chosenAvg.ToString("0.0")).Append(" inf/vert");
                if (droppedInf > 0) sb.Append(", ").Append(droppedInf).Append(" unmapped influences dropped");
                sb.Append(")\n");
            }

            // reload the active renderer's editable model so painting/undo pick up the new weights
            if (activeRenderer != null)
            {
                model = WeightModel.Load(activeRenderer);
                heatmap.Attach(activeRenderer, model);
                if (heatmap.Enabled) heatmap.Refresh();
                RebuildBoneList();
            }

            Debug.Log("[WeightStudio] Weight recovery from " + path + ":\n" + sb +
                      "Recovered: " + done + ", skipped: " + skipped + ", duplicate copies ignored: " + dupIgnored);
            SetStatus("Recovered weights onto " + done + " mesh(es)"
                      + (skipped > 0 ? ", " + skipped + " had no match" : "")
                      + (dupIgnored > 0 ? " (ignored " + dupIgnored + " duplicate copies)" : "")
                      + ". See log; now re-export for a clean file.");
        }

        // renderer-local VBX slot -> loaded renderer bone slot, matched by bone NAME
        static int[] BuildLocalToTarget(VbxMesh vm, VbxDoc doc, Dictionary<string, int> nameToSlot)
        {
            int len = vm.boneMap != null ? vm.boneMap.Length : 0;
            var map = new int[len];
            for (int s = 0; s < len; s++)
            {
                map[s] = -1;
                int gi = vm.boneMap[s];
                if (gi >= 0 && gi < doc.bones.Count)
                {
                    string bn = doc.bones[gi].name;
                    if (bn != null && nameToSlot.TryGetValue(bn, out int ts)) map[s] = ts;
                }
            }
            return map;
        }

        static float AvgInfluences(VbxMesh m)
        {
            if (m.bonesPerVertex == null || m.bonesPerVertex.Length == 0) return 0f;
            long sum = 0;
            for (int i = 0; i < m.bonesPerVertex.Length; i++) sum += m.bonesPerVertex[i];
            return (float)sum / m.bonesPerVertex.Length;
        }

        static GltfVrmExporter.VrmMeta MetaFor(Transform root)
        {
            var m = GltfVrmExporter.VrmMeta.Default();
            if (root != null) m.title = root.name;
            return m;
        }

        static Transform FindAvatarRoot(Transform t)
        {
            Animator a = t.GetComponentInParent<Animator>();
            if (a != null) return a.transform;
            Transform top = t;
            while (top.parent != null) top = top.parent;
            return top;
        }

        // Collect ONLY the skinned meshes that belong to the single chosen avatar.
        // FindObjectsOfType grabs every SMR in the scene: other avatars, a Pose Studio
        // copy, and our own heatmap overlay, which would duplicate meshes in an export.
        // Seed from the active/first mesh, find its avatar root, then keep just the
        // renderers parented under that root, skipping WeightStudio's helper overlays.
        List<SkinnedMeshRenderer> GatherAvatarRenderers(out Transform root)
        {
            root = null;
            SkinnedMeshRenderer seed = activeRenderer != null ? activeRenderer
                                     : (meshes.Count > 0 ? meshes[0] : null);
            if (seed == null) return new List<SkinnedMeshRenderer>();

            root = FindAvatarRoot(seed.transform);
            var result = new List<SkinnedMeshRenderer>();
            if (root == null) { result.Add(seed); return result; }

            var seen = new HashSet<SkinnedMeshRenderer>();
            foreach (var smr in FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (smr == null || smr.sharedMesh == null) continue;
                if (smr.name == "WeightStudio_Heatmap") continue;        // live overlay clone
                if (smr.gameObject.name == "WeightStudio_PickSurface") continue;
                if (smr.transform != root && !smr.transform.IsChildOf(root)) continue;
                if (seen.Add(smr)) result.Add(smr);
            }
            if (result.Count == 0) result.Add(seed);
            return result;
        }

        static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        // ------------------------------------------------------------ UI helpers

        void BindSliderFloat(string sliderName, string valueName, float val, float min, float max, Action<float> set, string fmt)
        {
            Slider s = Find<Slider>(sliderName);
            Text t = Find<Text>(valueName);
            if (s == null) return;
            s.wholeNumbers = false;
            s.minValue = min; s.maxValue = max;
            s.SetValueWithoutNotify(val);
            if (t != null) t.text = val.ToString(fmt, CultureInfo.InvariantCulture);
            s.onValueChanged.AddListener(v => { set(v); if (t != null) t.text = v.ToString(fmt, CultureInfo.InvariantCulture); });
        }

        void BindSliderInt(string sliderName, string valueName, int val, int min, int max, Action<int> set)
        {
            Slider s = Find<Slider>(sliderName);
            Text t = Find<Text>(valueName);
            if (s == null) return;
            s.wholeNumbers = true;
            s.minValue = min; s.maxValue = max;
            s.SetValueWithoutNotify(val);
            if (t != null) t.text = val.ToString();
            s.onValueChanged.AddListener(v => { int iv = Mathf.RoundToInt(v); set(iv); if (t != null) t.text = iv.ToString(); });
        }

        void BindButton(string name, UnityEngine.Events.UnityAction action)
        {
            Button b = Find<Button>(name);
            if (b != null) b.onClick.AddListener(action);
        }

        void BindToggle(string name, bool initial, Action<bool> set)
        {
            Toggle t = Find<Toggle>(name);
            if (t == null) return;
            t.SetIsOnWithoutNotify(initial);
            t.onValueChanged.AddListener(v => set(v));
        }

        T Find<T>(string name) where T : Component
        {
            if (window == null) return null;
            foreach (T c in window.GetComponentsInChildren<T>(true))
                if (c.gameObject.name == name) return c;
            return null;
        }

        GameObject MakeRow(RectTransform parent, string name, string label, float y, float h, Action onClick, Func<bool> isActive)
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
            return row;
        }

        // A small show/hide toggle pinned to the right of a mesh row. Toggles the
        // renderer's visibility (smr.enabled) so you can hide meshes that block your
        // view while weight painting, without removing them from the avatar.
        void AddVisibilityButton(GameObject row, SkinnedMeshRenderer smr)
        {
            GameObject b = new GameObject("Vis", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = b.GetComponent<RectTransform>();
            rt.SetParent(row.transform, false);
            rt.anchorMin = new Vector2(1f, 0.5f); rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(52f, 18f);
            rt.anchoredPosition = new Vector2(-2f, 0f);

            Image img = b.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.12f);
            Button btn = b.AddComponent<Button>();
            btn.targetGraphic = img;

            Text t = MakeRowText(b.transform, "");
            t.alignment = TextAnchor.MiddleCenter;
            t.fontSize = 11;
            t.rectTransform.offsetMin = new Vector2(2f, 0f);
            t.rectTransform.offsetMax = new Vector2(-2f, 0f);

            Action refresh = () =>
            {
                bool vis = smr != null && smr.enabled;
                t.text = vis ? "shown" : "hidden";
                t.color = vis ? new Color(0.7f, 1f, 0.7f, 1f) : new Color(1f, 0.55f, 0.55f, 1f);
                img.color = vis ? new Color(1f, 1f, 1f, 0.12f) : new Color(1f, 0.4f, 0.4f, 0.18f);
            };
            refresh();
            btn.onClick.AddListener(() =>
            {
                if (smr == null) return;
                smr.enabled = !smr.enabled;
                wsToggled.Add(smr.name);
                ReloadHiddenIfChanged();   // don't clobber other plugins' edits
                if (smr.enabled) hiddenMeshes.Remove(smr.name); else hiddenMeshes.Add(smr.name);
                SaveHiddenMeshes();
                refresh();
                SetStatus((smr.enabled ? "Showing '" : "Hidden '") + smr.name + "'.");
            });
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

        // ------------------------------------------------------------ selection-paint toggle (via keyboard)
        // Hold Left-Shift to paint a SELECTION instead of weights (for 'Smooth Selection').
        void LateUpdate()
        {
            PhaseNote("WS LateUpdate");

            bool wantSel = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (wantSel != buildingSelection)
            {
                buildingSelection = wantSel;
                UpdateBrushLabel();
                if (brushLabel != null && buildingSelection) brushLabel.text = "SELECT (shift)  " + CountSelection() + " verts";
            }

            // a paint stroke ends when the button is released → next stroke makes a fresh undo step
            if (stroking && !Input.GetMouseButton(0)) stroking = false;

            // Ctrl+Z = undo, Ctrl+Shift+Z = redo, Ctrl+Shift+R = recover weights from VBX
            if (window != null && window.activeSelf)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (ctrl && shift && Input.GetKeyDown(KeyCode.R))
                {
                    RecoverWeightsFromVbx();
                }
                else if (ctrl && shift && Input.GetKeyDown(KeyCode.E))
                {
                    ExportVbxFromFile();   // build a standalone VBX from a .vsfavatar file
                }
                // while the Collider Editor is open, Ctrl+Z belongs to it (ColEdUpdate), so a
                // collider undo does not also step back a weight edit
                else if (model != null && ctrl && Input.GetKeyDown(KeyCode.Z) &&
                         !(colEdWindow != null && colEdWindow.activeSelf))
                {
                    if (shift) DoRedo();
                    else DoUndo();
                }
            }
        }

        // ------------------------------------------------------------ settings

        void LoadSettings()
        {
            var s = VNyanInterface.VNyanInterface.VNyanSettings.loadSettings("WeightStudio.cfg");
            if (s == null) return;
            brush.radius = GetF(s, "radius", brush.radius);
            brush.strength = GetF(s, "strength", brush.strength);
            brush.falloff = GetF(s, "falloff", brush.falloff);
            smooth.maxBonesPerVertex = GetI(s, "maxbones", smooth.maxBonesPerVertex);
            smooth.iterations = GetI(s, "smoothiter", smooth.iterations);
            smooth.lambda = GetF(s, "smoothlambda", smooth.lambda);
            heatOpacity = GetF(s, "heatopacity", heatOpacity);
            xfer.samples = GetI(s, "xfersamples", xfer.samples);
            xfer.maxDistance = GetF(s, "xfermaxdist", xfer.maxDistance);
            xfer.blend = GetF(s, "xferblend", xfer.blend);
            xfer.mapping = (TransferMapping)GetI(s, "xfermap", (int)xfer.mapping);
            xfer.matchByName = GetI(s, "xfermatch", xfer.matchByName ? 1 : 0) != 0;
            xfer.selectedOnly = GetI(s, "xfersel", xfer.selectedOnly ? 1 : 0) != 0;
            xfer.normalize = GetI(s, "xfernorm", xfer.normalize ? 1 : 0) != 0;
            genCfg.radiusScale = GetF(s, "colradius", genCfg.radiusScale);
            genCfg.minWeight = GetF(s, "colminw", genCfg.minWeight);
            genCfg.minVerts = GetI(s, "colminv", genCfg.minVerts);
            genCfg.capsules = GetI(s, "colcaps", genCfg.capsules ? 1 : 0) != 0;
            genCfg.skipFingers = GetI(s, "colfingers", genCfg.skipFingers ? 1 : 0) != 0;
            genCfg.humanoidOnly = GetI(s, "colhuman", genCfg.humanoidOnly ? 1 : 0) != 0;
            includeColliders = GetI(s, "colembed", includeColliders ? 1 : 0) != 0;
        }

        void OnApplicationQuit() { SaveSettings(); }

        void OnDestroy() { if (glMat != null) Destroy(glMat); }

        void CreateGLMat()
        {
            // "Hidden/Internal-Colored" is the ideal line shader, but it's often stripped from
            // player builds (VNyan is a player build). Fall back to shaders that a uGUI app
            // always ships, so collider viz still works.
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) { Debug.LogWarning("[WeightStudio] No GL shader found, collider viz unavailable"); return; }
            glMat = new Material(sh);
            glMat.hideFlags = HideFlags.HideAndDontSave;
            glMat.SetInt("_ZWrite", 0);
            glMat.SetInt("_ZTest", 8); // Always: visible through avatar mesh
            glMat.SetInt("_Cull", 0);
            Debug.Log("[WeightStudio] GL collider material using shader: " + sh.name);
        }

        void OnToggleColliders()
        {
            showColliders = !showColliders;
            if (showCollidersText != null)
                showCollidersText.text = showColliders ? "Hide Colliders" : "Show Colliders";
            Debug.Log("[WeightStudio] ShowColliders toggled -> " + showColliders
                + " (glMat=" + (glMat != null ? glMat.shader.name : "NULL")
                + ", colliders=" + (genColliders != null ? genColliders.Count : 0) + ")");
            if (showColliders && (genColliders == null || genColliders.Count == 0))
                SetStatus("No colliders yet. Click Generate Colliders first.");
            else
                SetStatus(showColliders ? "Collider viz ON: wireframes drawn in viewport" : "Collider viz OFF");
        }

        void RebuildColliderBoneCache()
        {
            colliderBoneCache = new Dictionary<string, Transform>();
            if (colliderAvatarRoot == null) return;
            foreach (Transform t in colliderAvatarRoot.GetComponentsInChildren<Transform>(true))
                if (!colliderBoneCache.ContainsKey(t.name))
                    colliderBoneCache[t.name] = t;
        }

        void OnRenderObject()
        {
            bool editorOpen = colEdWindow != null && colEdWindow.activeSelf;
            if ((!showColliders && !editorOpen) || glMat == null) return;
            if (genColliders == null || genColliders.Count == 0) return;
            if (colliderBoneCache == null) return;

            glMat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);

            for (int i = 0; i < genColliders.Count; i++)
            {
                ColliderGen.GenCollider col = genColliders[i];
                Transform bone;
                colliderBoneCache.TryGetValue(col.boneName, out bone);

                Vector3 wCenter = bone != null ? bone.TransformPoint(col.center) : col.center;
                bool sel = editorOpen && i == colEdSel;

                if (col.shape == ColliderGen.GenShape.Sphere)
                {
                    GL.Color(sel ? new Color(1f, 0.25f, 1f, 1f) : new Color(0f, 1f, 0.85f, 1f));
                    DrawWireSphereGL(wCenter, col.radius, 20);
                }
                else
                {
                    GL.Color(sel ? new Color(1f, 0.25f, 1f, 1f) : new Color(1f, 0.85f, 0f, 1f));
                    Vector3 wEnd = bone != null ? bone.TransformPoint(col.centerEnd) : col.centerEnd;
                    DrawWireCapsuleGL(wCenter, wEnd, col.radius, 16);
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        static void DrawWireCircleGL(Vector3 center, Vector3 normal, float r, int segs)
        {
            Vector3 u = Vector3.Cross(normal, Vector3.up);
            if (u.sqrMagnitude < 0.001f) u = Vector3.Cross(normal, Vector3.forward);
            u.Normalize();
            Vector3 v = Vector3.Cross(normal, u).normalized;
            float step = 2f * Mathf.PI / segs;
            Vector3 prev = center + u * r;
            for (int i = 1; i <= segs; i++)
            {
                float a = i * step;
                Vector3 next = center + (Mathf.Cos(a) * u + Mathf.Sin(a) * v) * r;
                GL.Vertex(prev); GL.Vertex(next);
                prev = next;
            }
        }

        static void DrawWireSphereGL(Vector3 center, float r, int segs)
        {
            DrawWireCircleGL(center, Vector3.up, r, segs);
            DrawWireCircleGL(center, Vector3.right, r, segs);
            DrawWireCircleGL(center, Vector3.forward, r, segs);
        }

        static void DrawWireCapsuleGL(Vector3 a, Vector3 b, float r, int segs)
        {
            Vector3 ax = b - a;
            if (ax.sqrMagnitude < 1e-8f) { DrawWireSphereGL(a, r, segs); return; }
            ax.Normalize();
            DrawWireCircleGL(a, ax, r, segs);
            DrawWireCircleGL(b, ax, r, segs);
            Vector3 u = Vector3.Cross(ax, Vector3.up);
            if (u.sqrMagnitude < 0.001f) u = Vector3.Cross(ax, Vector3.forward);
            u.Normalize();
            Vector3 v = Vector3.Cross(ax, u).normalized;
            GL.Vertex(a + u * r); GL.Vertex(b + u * r);
            GL.Vertex(a - u * r); GL.Vertex(b - u * r);
            GL.Vertex(a + v * r); GL.Vertex(b + v * r);
            GL.Vertex(a - v * r); GL.Vertex(b - v * r);
        }

        void SaveSettings()
        {
            var s = new Dictionary<string, string>
            {
                ["radius"] = brush.radius.ToString(CultureInfo.InvariantCulture),
                ["strength"] = brush.strength.ToString(CultureInfo.InvariantCulture),
                ["falloff"] = brush.falloff.ToString(CultureInfo.InvariantCulture),
                ["maxbones"] = smooth.maxBonesPerVertex.ToString(CultureInfo.InvariantCulture),
                ["smoothiter"] = smooth.iterations.ToString(CultureInfo.InvariantCulture),
                ["smoothlambda"] = smooth.lambda.ToString(CultureInfo.InvariantCulture),
                ["heatopacity"] = heatOpacity.ToString(CultureInfo.InvariantCulture),
                ["xfersamples"] = xfer.samples.ToString(CultureInfo.InvariantCulture),
                ["xfermaxdist"] = xfer.maxDistance.ToString(CultureInfo.InvariantCulture),
                ["xferblend"] = xfer.blend.ToString(CultureInfo.InvariantCulture),
                ["xfermap"] = ((int)xfer.mapping).ToString(CultureInfo.InvariantCulture),
                ["xfermatch"] = (xfer.matchByName ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                ["xfersel"] = (xfer.selectedOnly ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                ["xfernorm"] = (xfer.normalize ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                ["colradius"] = genCfg.radiusScale.ToString(CultureInfo.InvariantCulture),
                ["colminw"] = genCfg.minWeight.ToString(CultureInfo.InvariantCulture),
                ["colminv"] = genCfg.minVerts.ToString(CultureInfo.InvariantCulture),
                ["colcaps"] = (genCfg.capsules ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                ["colfingers"] = (genCfg.skipFingers ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                ["colhuman"] = (genCfg.humanoidOnly ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                ["colembed"] = (includeColliders ? 1 : 0).ToString(CultureInfo.InvariantCulture),
            };
            VNyanInterface.VNyanInterface.VNyanSettings.saveSettings("WeightStudio.cfg", s);
        }

        static float GetF(Dictionary<string, string> s, string k, float def) =>
            s.TryGetValue(k, out string v) && float.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out float r) ? r : def;
        static int GetI(Dictionary<string, string> s, string k, int def) =>
            s.TryGetValue(k, out string v) && int.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out int r) ? r : def;
    }
}
