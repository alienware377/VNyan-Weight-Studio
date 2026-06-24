using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Builds the Weight Studio plugin window + starter prefab and bundles them into a
// .vnobj AssetBundle. Run headlessly via:
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod WeightStudioBuild.Build
//
// The window prefab honors the control-name contract expected by
// VNyanWeightStudio.BoneWeightStudioPlugin (found at runtime via Find<T>).
public static class WeightStudioBuild
{
    public static void Build()
    {
        const string windowPrefabPath = "Assets/WeightStudioWindow.prefab";
        const string starterPrefabPath = "Assets/WeightStudioStarter.prefab";
        const string bundleName = "vnyanweightstudio";
        const string outDir = "AssetBundles";

        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);

        // Starter prefab carries the plugin component, pointed at the window.
        // VNyan loads the bundle's addressable "vnyanitem"; root name "VNyanTemp"
        // matches the official SDK output.
        GameObject go = new GameObject("VNyanTemp");
        var plugin = go.AddComponent<VNyanWeightStudio.BoneWeightStudioPlugin>();
        plugin.windowPrefab = windowAsset;
        GameObject starterAsset = PrefabUtility.SaveAsPrefabAsset(go, starterPrefabPath);
        Object.DestroyImmediate(go);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AssetBundleBuild abb = new AssetBundleBuild
        {
            assetBundleName = bundleName,
            assetNames = new[] { starterPrefabPath },
            addressableNames = new[] { "vnyanitem" }
        };

        Directory.CreateDirectory(outDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outDir, new[] { abb }, BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("[WeightStudioBuild] BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        string built = Path.Combine(outDir, bundleName);
        string final = Path.Combine(outDir, "WeightStudio.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(built, final);
        Debug.Log("[WeightStudioBuild] wrote " + final);
        EditorApplication.Exit(0);
    }

    // Slider rows: key (used for Slider_<key> + Value_<key>), label, min, max, whole, init, fmt.
    static readonly string[] KEY   = { "radius", "strength", "falloff", "maxbones", "smoothiter", "smoothlambda" };
    static readonly string[] LBL   = { "Brush radius", "Strength", "Falloff", "Max bones / vert", "Smooth iters", "Smooth lambda" };
    static readonly float[]  MIN   = { 0.005f, 0f, 0f, 4f, 1f, 0f };
    static readonly float[]  MAX   = { 0.4f, 1f, 1f, 16f, 30f, 1f };
    static readonly bool[]   WHOLE = { false, false, false, true, true, false };
    static readonly float[]  INIT  = { 0.04f, 0.5f, 0.6f, 8f, 4f, 0.5f };
    static readonly string[] FMT   = { "0.000", "0.00", "0.00", "0", "0", "0.00" };

    static DefaultControls.Resources _res;

    static GameObject BuildWindowPrefab(string prefabPath)
    {
        _res = new DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
            mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd")
        };

        const float W = 460f;
        const float P = 12f;
        const float contentX = P;
        const float contentW = W - 2f * P;

        GameObject root = new GameObject("WeightStudioWindow",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.13f, 0.13f, 0.15f, 0.96f);
        root.AddComponent<VNyanWeightStudio.WindowDrag>();

        // ---- fixed window frame ----
        // The window is a FIXED height. A pinned Title sits at the top; a pinned
        // tooltip panel + Close button sit at the bottom; everything else lives in a
        // vertical ScrollRect (with a visible scrollbar) so the panel never outgrows
        // the VNyan window no matter how many tools we add.
        const float headerH = 34f;   // pinned title strip
        const float scrollH = 466f;  // visible body viewport height
        const float tipH    = 56f;   // pinned tooltip panel
        const float closeH  = 28f;   // pinned close button
        const float WH = headerH + scrollH + 8f + tipH + 8f + closeH + P;

        // pinned title
        MakeText(root.transform, "Title", "Weight Studio",
            contentX, 8f, contentW, 22f, 15, TextAnchor.MiddleCenter, FontStyle.Bold);

        // scrolling body (all tools live inside this)
        RectTransform body = MakeScroll(root.transform, "BodyScrollView", "BodyContent",
            contentX, headerH, contentW, scrollH, true);

        // body-local layout space: x starts near 0, width leaves room for the scrollbar
        const float bx = 2f;
        float bw = contentW - 18f;   // viewport width minus insets + scrollbar gutter
        float y = 4f;

        MakeText(body, "Label_Status", "ready",
            bx, y, bw, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        y += 22f;

        MakeText(body, "Label_Brush", "idle",
            bx, y, bw, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Bold);
        y += 26f;

        // ---- sliders ----
        for (int i = 0; i < KEY.Length; i++)
        {
            SliderRow(body, KEY[i], LBL[i], MIN[i], MAX[i], WHOLE[i], INIT[i], FMT[i], bx, bw, y, true);
            y += 30f;
        }

        y += 4f;
        MakeToggle(body, "Toggle_paint", "Paint mode (hold LMB on model; Shift = select)",
            bx, y, bw - 20f, 24f);
        MakeHelp(body, "paint", bx + bw - 16f, y + 4f);
        y += 28f;

        MakeText(body, "Lbl_BrushModes", "Brush mode", bx, y, bw - 20f, 20f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeHelp(body, "brushmodes", bx + bw - 16f, y + 2f);
        y += 22f;

        // ---- brush mode buttons (5 across) ----
        const float mgap = 6f;
        float mbw = (bw - 4f * mgap) / 5f;
        MakeButton(body, "Button_Add",     "Add",     bx + 0f * (mbw + mgap), y, mbw, 26f);
        MakeButton(body, "Button_Sub",     "Sub",     bx + 1f * (mbw + mgap), y, mbw, 26f);
        MakeButton(body, "Button_Replace", "Replace", bx + 2f * (mbw + mgap), y, mbw, 26f);
        MakeButton(body, "Button_Smooth",  "Smooth",  bx + 3f * (mbw + mgap), y, mbw, 26f);
        MakeButton(body, "Button_Blur",    "Blur",    bx + 4f * (mbw + mgap), y, mbw, 26f);
        y += 32f;

        // ---- heatmap overlay ----
        MakeToggle(body, "Toggle_heatmap", "Heatmap overlay (live weight colours)",
            bx, y, bw - 20f, 24f);
        MakeHelp(body, "heatmap", bx + bw - 16f, y + 4f);
        y += 28f;
        SliderRow(body, "heatopacity", "Overlay opacity", 0f, 1f, false, 0.75f, "0.00", bx, bw, y, true);
        y += 32f;

        // ---- mesh list ----
        MakeText(body, "Lbl_Meshes", "Meshes", bx, y, bw - 110f, 22f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeHelp(body, "meshes", bx + bw - 104f, y + 3f);
        MakeButton(body, "Button_RescanMesh", "Rescan", bx + bw - 84f, y, 84f, 22f);
        y += 26f;
        MakeScroll(body, "MeshScrollView", "MeshContent", bx, y, bw, 110f);
        y += 110f + 8f;

        // ---- bone list ----
        MakeText(body, "Lbl_Bones", "Bones (active = brush target)", bx, y, bw - 110f, 22f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeHelp(body, "bones", bx + bw - 104f, y + 3f);
        MakeButton(body, "Button_RescanBone", "Refresh", bx + bw - 84f, y, 84f, 22f);
        y += 26f;
        MakeScroll(body, "BoneScrollView", "BoneContent", bx, y, bw, 130f);
        y += 130f + 8f;

        // ---- tool rows ----
        const float gap = 8f;
        float bw3 = (bw - 2f * gap) / 3f;

        MakeButtonWithHelp(body, "Button_SmoothMesh", "Smooth Mesh", "smoothmesh", bx + 0f * (bw3 + gap), y, bw3, 28f);
        MakeButtonWithHelp(body, "Button_SmoothSel",  "Smooth Sel",  "smoothsel",  bx + 1f * (bw3 + gap), y, bw3, 28f);
        MakeButtonWithHelp(body, "Button_LimitInf",   "Limit Inf",   "limitinf",   bx + 2f * (bw3 + gap), y, bw3, 28f);
        y += 32f;

        // ---- smooth the active bone only ----
        float bwsb = (bw - gap) / 2f;
        MakeButtonWithHelp(body, "Button_SmoothBone",    "Smooth Bone",     "smoothbone",    bx + 0f * (bwsb + gap), y, bwsb, 28f);
        MakeButtonWithHelp(body, "Button_SmoothBoneSel", "Smooth Bone Sel", "smoothbonesel", bx + 1f * (bwsb + gap), y, bwsb, 28f);
        y += 32f;

        MakeButtonWithHelp(body, "Button_Apply",    "Apply",     "apply",   bx + 0f * (bw3 + gap), y, bw3, 28f);
        MakeButtonWithHelp(body, "Button_Revert",   "Revert",    "revert",  bx + 1f * (bw3 + gap), y, bw3, 28f);
        MakeButtonWithHelp(body, "Button_ClearSel", "Clear Sel", "clearsel", bx + 2f * (bw3 + gap), y, bw3, 28f);
        y += 32f;

        // ---- undo / redo ----
        float bwu = (bw - gap) / 2f;
        MakeButtonWithHelp(body, "Button_Undo", "Undo", "undo", bx + 0f * (bwu + gap), y, bwu, 28f);
        MakeButtonWithHelp(body, "Button_Redo", "Redo", "redo", bx + 1f * (bwu + gap), y, bwu, 28f);
        y += 36f;

        // ---- weight transfer ----
        MakeText(body, "Lbl_Transfer", "Weight Transfer (copy between meshes)",
            bx, y, bw - 20f, 20f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeHelp(body, "xfersource", bx + bw - 16f, y + 2f);
        y += 24f;

        MakeText(body, "Label_Source", "Source: (none — pick a mesh, Set Source)",
            bx, y, bw - 128f, 22f, 11, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeButton(body, "Button_SetSource", "Set Source", bx + bw - 124f, y, 124f, 22f);
        y += 26f;

        MakeText(body, "Lbl_Map", "Mapping", bx, y, 60f, 24f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeHelp(body, "xfermap", bx + 60f, y + 4f);
        float mapx = bx + 82f;
        float mapw = (bx + bw - mapx - 2f * gap) / 3f;
        MakeButton(body, "Button_MapNearest", "Nearest", mapx + 0f * (mapw + gap), y, mapw, 24f);
        MakeButton(body, "Button_MapSmooth",  "Smooth",  mapx + 1f * (mapw + gap), y, mapw, 24f);
        MakeButton(body, "Button_MapSurface", "Surface", mapx + 2f * (mapw + gap), y, mapw, 24f);
        y += 28f;

        SliderRow(body, "xfersamples", "Smooth samples",  1f, 16f, true,  4f, "0",     bx, bw, y, true); y += 30f;
        SliderRow(body, "xfermaxdist", "Max distance (m)", 0f, 1f, false, 0f, "0.000", bx, bw, y, true); y += 30f;
        SliderRow(body, "xferblend",   "Blend amount",     0f, 1f, false, 1f, "0.00",  bx, bw, y, true); y += 30f;

        MakeToggle(body, "Toggle_xfermatch", "Match bones by name", bx, y, bw - 20f, 22f);
        MakeHelp(body, "xfermatch", bx + bw - 16f, y + 3f); y += 24f;
        MakeToggle(body, "Toggle_xfersel", "Selected target verts only", bx, y, bw - 20f, 22f);
        MakeHelp(body, "xfersel", bx + bw - 16f, y + 3f); y += 24f;
        MakeToggle(body, "Toggle_xfernorm", "Normalize after transfer", bx, y, bw - 20f, 22f);
        MakeHelp(body, "xfernorm", bx + bw - 16f, y + 3f); y += 26f;

        MakeButtonWithHelp(body, "Button_Transfer", "Transfer Weights", "xfergo", bx, y, bw, 30f);
        y += 36f;

        // ---- colliders (auto-fit) ----
        MakeText(body, "Lbl_Colliders", "Colliders (auto-fit physics shapes)",
            bx, y, bw, 20f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        y += 24f;

        MakeText(body, "Label_Colliders", "no colliders generated",
            bx, y, bw, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        y += 22f;

        SliderRow(body, "colradius", "Radius scale",     0.3f, 2.5f,  false, 1f,   "0.00", bx, bw, y, true); y += 30f;
        SliderRow(body, "colminw",   "Min bone weight",  0.1f, 0.95f, false, 0.5f, "0.00", bx, bw, y, true); y += 30f;
        SliderRow(body, "colminv",   "Min verts / bone", 4f,   64f,   true,  12f,  "0",    bx, bw, y, true); y += 30f;

        MakeToggle(body, "Toggle_colcaps", "Use capsules for long bones", bx, y, bw - 20f, 22f);
        MakeHelp(body, "colcaps", bx + bw - 16f, y + 3f); y += 24f;
        MakeToggle(body, "Toggle_colfingers", "Skip finger bones", bx, y, bw - 20f, 22f);
        MakeHelp(body, "colfingers", bx + bw - 16f, y + 3f); y += 24f;
        MakeToggle(body, "Toggle_colhuman", "Humanoid bones only", bx, y, bw - 20f, 22f);
        MakeHelp(body, "colhuman", bx + bw - 16f, y + 3f); y += 24f;
        MakeToggle(body, "Toggle_colembed", "Embed colliders in exports (VBX/VRM)", bx, y, bw - 20f, 22f);
        MakeHelp(body, "colembed", bx + bw - 16f, y + 3f); y += 26f;

        MakeButtonWithHelp(body, "Button_GenColliders", "Generate Colliders", "colgen", bx, y, bw, 30f);
        y += 34f;

        float bw2 = (bw - gap) / 2f;
        MakeButtonWithHelp(body, "Button_ExportColliders", "Export Sidecars",     "colsidecar",  bx + 0f * (bw2 + gap), y, bw2, 28f);
        MakeButtonWithHelp(body, "Button_ExportUnityPkg",  ".unitypackage",       "colunitypkg", bx + 1f * (bw2 + gap), y, bw2, 28f);
        y += 36f;

        // ---- export row ----
        MakeText(body, "Lbl_Export", "Export", bx, y, bw - 20f, 20f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeHelp(body, "export", bx + bw - 16f, y + 2f);
        y += 24f;
        MakeButton(body, "Button_ExportVrm0", "VRM 0.x",  bx + 0f * (bw3 + gap), y, bw3, 30f);
        MakeButton(body, "Button_ExportVrm1", "VRM 1.0",  bx + 1f * (bw3 + gap), y, bw3, 30f);
        MakeButton(body, "Button_ExportVbx",  "VBX (>4)", bx + 2f * (bw3 + gap), y, bw3, 30f);
        y += 34f + 4f;

        // size the scroll content to the full body height so it scrolls
        body.sizeDelta = new Vector2(0f, y);

        // ---- pinned tooltip panel ----
        float footY = headerH + scrollH + 8f;
        GameObject tipBg = new GameObject("TipPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        Image tib = tipBg.GetComponent<Image>();
        tib.sprite = _res.background; tib.type = Image.Type.Sliced;
        tib.color = new Color(0.06f, 0.08f, 0.10f, 0.95f);
        Place(tipBg.transform, root.transform, contentX, footY, contentW, tipH);
        Text tip = MakeText(tipBg.transform, "Label_Tip", "Tap any  ?  for a plain-language explanation.",
            6f, 2f, contentW - 12f, tipH - 4f, 11, TextAnchor.UpperLeft, FontStyle.Normal);
        tip.horizontalOverflow = HorizontalWrapMode.Wrap;
        tip.verticalOverflow = VerticalWrapMode.Truncate;

        // ---- pinned close ----
        MakeButton(root.transform, "Button_Close", "Close", contentX, footY + tipH + 8f, contentW, closeH);

        rrt.sizeDelta = new Vector2(W, WH);

        GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return asset;
    }

    static RectTransform Place(Transform t, Transform parent, float x, float y, float w, float h)
    {
        RectTransform rt = t as RectTransform ?? t.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, -y);
        return rt;
    }

    static Text MakeText(Transform parent, string name, string text,
        float x, float y, float w, float h, int size, TextAnchor anchor, FontStyle style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        Text t = go.GetComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = anchor;
        t.color = Color.white;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        Place(go.transform, parent, x, y, w, h);
        return t;
    }

    static void MakeToggle(Transform parent, string name, string label, float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateToggle(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        Transform lbl = go.transform.Find("Label");
        if (lbl != null)
        {
            Text lt = lbl.GetComponent<Text>();
            if (lt != null) { lt.text = label; lt.color = Color.white; lt.fontSize = 12; }
        }
    }

    // Builds a vertical scroll view named <name> with a "Viewport" (RectMask2D) and
    // a content child named <contentName> (top-stretch, pivot top). The runtime
    // plugin finds <contentName> by name and fills it with selectable rows.
    static RectTransform MakeScroll(Transform parent, string name, string contentName, float x, float y, float w, float h, bool withBar = false)
    {
        GameObject sv = new GameObject(name,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
        Image svBg = sv.GetComponent<Image>();
        svBg.sprite = _res.background;
        svBg.type = Image.Type.Sliced;
        svBg.color = new Color(0.08f, 0.08f, 0.10f, 0.92f);
        Place(sv.transform, parent, x, y, w, h);

        float barW = withBar ? 12f : 0f;

        GameObject vp = new GameObject("Viewport",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        RectTransform vprt = vp.GetComponent<RectTransform>();
        vprt.SetParent(sv.transform, false);
        vprt.anchorMin = new Vector2(0f, 0f);
        vprt.anchorMax = new Vector2(1f, 1f);
        vprt.pivot = new Vector2(0f, 1f);
        vprt.offsetMin = new Vector2(2f, 2f);
        vprt.offsetMax = new Vector2(-2f - barW, -2f);
        vp.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f);

        GameObject content = new GameObject(contentName, typeof(RectTransform));
        RectTransform crt = content.GetComponent<RectTransform>();
        crt.SetParent(vp.transform, false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0f, 1f);
        crt.sizeDelta = new Vector2(0f, 0f);
        crt.anchoredPosition = Vector2.zero;

        ScrollRect sr = sv.GetComponent<ScrollRect>();
        sr.viewport = vprt;
        sr.content = crt;
        sr.horizontal = false;
        sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 18f;

        if (withBar)
        {
            GameObject sb = DefaultControls.CreateScrollbar(_res);
            sb.name = name + "_Bar";
            Scrollbar sc = sb.GetComponent<Scrollbar>();
            sc.direction = Scrollbar.Direction.BottomToTop;
            RectTransform sbrt = sb.GetComponent<RectTransform>();
            sbrt.SetParent(sv.transform, false);
            sbrt.anchorMin = new Vector2(1f, 0f);
            sbrt.anchorMax = new Vector2(1f, 1f);
            sbrt.pivot = new Vector2(1f, 0.5f);
            sbrt.sizeDelta = new Vector2(barW, -4f);
            sbrt.anchoredPosition = new Vector2(-2f, 0f);
            sr.verticalScrollbar = sc;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }
        return crt;
    }

    static void MakeButton(Transform parent, string name, string label, float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateButton(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        Text bt = go.GetComponentInChildren<Text>(true);
        if (bt != null) { bt.text = label; bt.color = Color.black; bt.fontSize = 12; }
    }

    // A tiny "?" button named Help_<id>; the runtime wires it to show a tooltip.
    static void MakeHelp(Transform parent, string id, float x, float y)
    {
        GameObject go = DefaultControls.CreateButton(_res);
        go.name = "Help_" + id;
        Place(go.transform, parent, x, y, 16f, 16f);
        Text t = go.GetComponentInChildren<Text>(true);
        if (t != null) { t.text = "?"; t.color = Color.black; t.fontSize = 11; t.fontStyle = FontStyle.Bold; }
    }

    // A button with a leading "?" help button.
    static void MakeButtonWithHelp(Transform parent, string name, string label, string helpId, float x, float y, float w, float h)
    {
        MakeHelp(parent, helpId, x, y + (h - 16f) * 0.5f);
        MakeButton(parent, name, label, x + 18f, y, w - 18f, h);
    }

    // Label + slider + value row (Slider_<key>, Value_<key>), with optional leading "?".
    static void SliderRow(Transform parent, string key, string label, float min, float max, bool whole,
        float init, string fmt, float cx, float cw, float y, bool help)
    {
        float lx = cx;
        if (help) { MakeHelp(parent, key, cx, y + 6f); lx = cx + 20f; }

        MakeText(parent, "Lbl_" + key, label, lx, y, 118f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);

        float sx = lx + 122f;
        float sw = (cx + cw - 56f) - sx;
        GameObject sl = DefaultControls.CreateSlider(_res);
        sl.name = "Slider_" + key;
        Slider sc = sl.GetComponent<Slider>();
        sc.minValue = min; sc.maxValue = max; sc.wholeNumbers = whole; sc.value = init;
        Place(sl.transform, parent, sx, y + 6f, sw, 18f);

        MakeText(parent, "Value_" + key, init.ToString(fmt), cx + cw - 52f, y, 52f, 28f, 12, TextAnchor.MiddleRight, FontStyle.Normal);
    }
}
