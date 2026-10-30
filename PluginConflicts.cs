using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Warns when another plugin is changing the avatar underneath Weight Studio.
    /// Soft-body plugins hide the real mesh (forceRenderingOff) and draw a reshaped copy
    /// (a "_SquishProxy"-style child), so the heatmap and the brush sit on the un-squished
    /// shape, hidden under the visible garment. Weight plugins (Bone Weight Smoother,
    /// JigglyPhys) swap in reweighted copies, so painted and saved weights include their
    /// work, and they run again on the next load. Detection looks only at what is on the
    /// avatar right now, so an installed but idle plugin raises nothing.
    /// The note is docked under the window (above it near the screen bottom), never over
    /// the controls; "Got it" hides it until the set of problems changes.
    /// </summary>
    public partial class BoneWeightStudioPlugin
    {
        static readonly string[] SoftBodySuffixes =
            { "_SquishProxy", "_WobbleProxy", "_JelloProxy", "_SoftBodyProxy", "_FieldProxy", "_JelloFollow" };

        GameObject conflictBanner;
        Text conflictText;
        string conflictKey = "";
        string conflictDismissed = "";
        float lastConflictCheck = -10f;

        void CheckConflictsTick()
        {
            if (window == null || !window.activeSelf)
            {
                if (conflictBanner != null && conflictBanner.activeSelf) conflictBanner.SetActive(false);
                return;
            }
            if (Time.unscaledTime - lastConflictCheck < 2f) return;
            lastConflictCheck = Time.unscaledTime;
            try { ShowConflicts(FindConflicts()); }
            catch (System.Exception e) { Debug.LogWarning("[WeightStudio] conflict check failed: " + e.Message); }
        }

        List<string> FindConflicts()
        {
            var lines = new List<string>();
            Transform root;
            List<SkinnedMeshRenderer> smrs = GatherAvatarRenderers(out root);
            if (smrs.Count == 0) return lines;

            var soft = new List<string>();
            var smoothed = new List<string>();
            var jiggly = new List<string>();
            foreach (SkinnedMeshRenderer smr in smrs)
            {
                if (smr == null) continue;
                if (smr.enabled && smr.forceRenderingOff && !IsSoftProxy(smr.name)) AddOnce(soft, smr.name);
                string mn = smr.sharedMesh != null ? smr.sharedMesh.name : "";
                if (mn.Contains("(smoothed weights)")) AddOnce(smoothed, smr.name);
                if (mn.Contains("(JigglyPhys)")) AddOnce(jiggly, smr.name);
            }
            if (root != null)
                foreach (Transform t in root.GetComponentsInChildren<Transform>(false))
                    if (IsSoftProxy(t.name) && t.parent != null) AddOnce(soft, t.parent.name);

            if (soft.Count > 0)
                lines.Add("Soft Body is reshaping " + Names(soft) + ". The heatmap and your brush follow the normal shape underneath, " +
                          "so they hide under the squished mesh and strokes can land in the wrong spot. Turn Soft Body off while you paint.");
            if (smoothed.Count > 0)
                lines.Add("Bone Weight Smoother already changed " + Names(smoothed) + ". Anything you paint and save includes its smoothing, " +
                          "and it smooths again on the next load. Turn it off while you paint.");
            if (jiggly.Count > 0)
                lines.Add("JigglyPhys has changed the weights on " + Names(jiggly) + ". Turn it off while you paint, or its changes get saved with yours.");
            return lines;
        }

        static bool IsSoftProxy(string n)
        {
            foreach (string s in SoftBodySuffixes) if (n.EndsWith(s)) return true;
            return false;
        }

        static void AddOnce(List<string> l, string s) { if (!l.Contains(s)) l.Add(s); }

        static string Names(List<string> l)
        {
            if (l.Count == 1) return "'" + l[0] + "'";
            if (l.Count <= 3) return "'" + string.Join("', '", l.ToArray()) + "'";
            return "'" + l[0] + "', '" + l[1] + "' and " + (l.Count - 2) + " more";
        }

        void ShowConflicts(List<string> lines)
        {
            string key = string.Join("\n", lines.ToArray());
            if (lines.Count == 0)
            {
                conflictKey = "";
                conflictDismissed = "";
                if (conflictBanner != null) conflictBanner.SetActive(false);
                return;
            }
            if (key != conflictKey)
            {
                conflictKey = key;
                Debug.Log("[WeightStudio] other plugins are changing the avatar: " + key.Replace("\n", " | "));
            }
            if (key == conflictDismissed) { if (conflictBanner != null) conflictBanner.SetActive(false); return; }
            EnsureConflictBanner();
            if (conflictBanner == null) return;
            conflictText.text = "Heads up: other plugins are changing this avatar.\n\n" + string.Join("\n\n", lines.ToArray());
            PlaceConflictBanner();
            conflictBanner.SetActive(true);
            conflictBanner.transform.SetAsLastSibling();
        }

        void EnsureConflictBanner()
        {
            if (conflictBanner != null || window == null) return;
            conflictBanner = new GameObject("WS_ConflictBanner", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = conflictBanner.GetComponent<RectTransform>();
            rt.SetParent(window.transform, false);
            conflictBanner.GetComponent<Image>().color = new Color(0.32f, 0.10f, 0.12f, 0.97f);

            conflictText = MakeRowText(conflictBanner.transform, "");
            RectTransform trt = conflictText.rectTransform;
            trt.offsetMin = new Vector2(12f, 40f);
            trt.offsetMax = new Vector2(-12f, -10f);
            conflictText.fontSize = 14;
            conflictText.alignment = TextAnchor.UpperLeft;
            conflictText.horizontalOverflow = HorizontalWrapMode.Wrap;
            conflictText.verticalOverflow = VerticalWrapMode.Overflow;
            conflictText.color = new Color(1f, 0.86f, 0.86f, 1f);

            GameObject b = new GameObject("Button_ConflictOk", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform brt = b.GetComponent<RectTransform>();
            brt.SetParent(conflictBanner.transform, false);
            brt.anchorMin = new Vector2(1f, 0f); brt.anchorMax = new Vector2(1f, 0f); brt.pivot = new Vector2(1f, 0f);
            brt.sizeDelta = new Vector2(100f, 26f);
            brt.anchoredPosition = new Vector2(-10f, 8f);
            Image bi = b.GetComponent<Image>();
            bi.color = new Color(1f, 1f, 1f, 0.9f);
            Button btn = b.AddComponent<Button>();
            btn.targetGraphic = bi;
            Text bt = MakeRowText(b.transform, "Got it");
            bt.alignment = TextAnchor.MiddleCenter;
            bt.color = new Color(0.15f, 0.15f, 0.18f, 1f);
            bt.rectTransform.offsetMin = Vector2.zero;
            bt.rectTransform.offsetMax = Vector2.zero;
            btn.onClick.AddListener(() =>
            {
                conflictDismissed = conflictKey;
                conflictBanner.SetActive(false);
            });
        }

        // under the window; above it when there is no room below
        void PlaceConflictBanner()
        {
            RectTransform win = window.transform as RectTransform;
            RectTransform rt = conflictBanner.GetComponent<RectTransform>();
            float w = win.rect.width - 24f;
            TextGenerationSettings gs = conflictText.GetGenerationSettings(new Vector2(Mathf.Max(100f, w), 0f));
            float textH = conflictText.cachedTextGeneratorForLayout.GetPreferredHeight(conflictText.text, gs) / Mathf.Max(0.01f, conflictText.pixelsPerUnit);
            float h = textH + 52f;

            var corners = new Vector3[4];
            win.GetWorldCorners(corners);
            Canvas canvas = win.GetComponentInParent<Canvas>();
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            float bottom = RectTransformUtility.WorldToScreenPoint(cam, corners[0]).y;
            float scale = win.lossyScale.y;
            if (cam != null && canvas != null) scale = canvas.scaleFactor;
            bool below = bottom - (h + 8f) * Mathf.Max(0.01f, scale) >= 0f;

            rt.anchorMin = new Vector2(0f, below ? 0f : 1f);
            rt.anchorMax = new Vector2(1f, below ? 0f : 1f);
            rt.pivot = new Vector2(0.5f, below ? 1f : 0f);
            rt.sizeDelta = new Vector2(0f, h);
            rt.anchoredPosition = new Vector2(0f, below ? -6f : 6f);
        }
    }
}
