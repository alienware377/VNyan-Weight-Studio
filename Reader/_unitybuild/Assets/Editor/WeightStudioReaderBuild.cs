using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Builds the Weight Studio Reader plugin window + starter prefab and bundles them
// into a .vnobj AssetBundle. Run headlessly via:
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod WeightStudioReaderBuild.Build
//
// Honors the control-name contract expected by
// VNyanWeightStudioReader.WeightStudioReaderPlugin (found at runtime via Find<T>).
public static class WeightStudioReaderBuild
{
    public static void Build()
    {
        const string windowPrefabPath = "Assets/WeightStudioReaderWindow.prefab";
        const string starterPrefabPath = "Assets/WeightStudioReaderStarter.prefab";
        const string bundleName = "vnyanweightstudioreader";
        const string outDir = "AssetBundles";

        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);

        GameObject go = new GameObject("VNyanTemp");
        var plugin = go.AddComponent<VNyanWeightStudioReader.WeightStudioReaderPlugin>();
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
            Debug.LogError("[WeightStudioReaderBuild] BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        string built = Path.Combine(outDir, bundleName);
        string final = Path.Combine(outDir, "WeightStudioReader.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(built, final);
        Debug.Log("[WeightStudioReaderBuild] wrote " + final);
        EditorApplication.Exit(0);
    }

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

        const float W = 420f;
        const float P = 12f;
        const float contentX = P;
        const float contentW = W - 2f * P;

        GameObject root = new GameObject("WeightStudioReaderWindow",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.13f, 0.13f, 0.15f, 0.96f);
        root.AddComponent<VNyanWeightStudioReader.WindowDrag>();

        float y = 10f;

        MakeText(root.transform, "Title", "Weight Studio Reader",
            contentX, y, contentW, 22f, 15, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 28f;

        MakeText(root.transform, "Label_Status", "ready",
            contentX, y, contentW, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        y += 24f;

        MakeText(root.transform, "Lbl_Files", "VBX files  (newest first)",
            contentX, y, contentW - 90f, 22f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeButton(root.transform, "Button_Rescan", "Rescan", contentX + contentW - 84f, y, 84f, 22f);
        y += 26f;

        MakeScroll(root.transform, "FileScrollView", "FileContent", contentX, y, contentW, 200f);
        y += 200f + 10f;

        const float gap = 8f;
        float bw2 = (contentW - gap) / 2f;
        MakeButton(root.transform, "Button_LoadSel",    "Load Selected", contentX, y, bw2, 30f);
        MakeButton(root.transform, "Button_RemoveLast", "Remove Last",   contentX + (bw2 + gap), y, bw2, 30f);
        y += 34f;

        MakeButton(root.transform, "Button_OpenFolder", "Open Folder", contentX, y, bw2, 28f);
        MakeButton(root.transform, "Button_Close",      "Close",       contentX + (bw2 + gap), y, bw2, 28f);
        y += 28f + P;

        rrt.sizeDelta = new Vector2(W, y);

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

    static RectTransform MakeScroll(Transform parent, string name, string contentName, float x, float y, float w, float h)
    {
        GameObject sv = new GameObject(name,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
        Image svBg = sv.GetComponent<Image>();
        svBg.sprite = _res.background;
        svBg.type = Image.Type.Sliced;
        svBg.color = new Color(0.08f, 0.08f, 0.10f, 0.92f);
        Place(sv.transform, parent, x, y, w, h);

        GameObject vp = new GameObject("Viewport",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        RectTransform vprt = vp.GetComponent<RectTransform>();
        vprt.SetParent(sv.transform, false);
        vprt.anchorMin = new Vector2(0f, 0f);
        vprt.anchorMax = new Vector2(1f, 1f);
        vprt.pivot = new Vector2(0f, 1f);
        vprt.offsetMin = new Vector2(2f, 2f);
        vprt.offsetMax = new Vector2(-2f, -2f);
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
        return crt;
    }

    static void MakeButton(Transform parent, string name, string label, float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateButton(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        Text bt = go.GetComponentInChildren<Text>(true);
        if (bt != null) { bt.text = label; bt.color = Color.black; bt.fontSize = 13; }
    }
}
