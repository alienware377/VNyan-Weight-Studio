using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Live weight-paint heatmap overlay, à la Blender's weight-paint mode.
    ///
    /// Spawns a second SkinnedMeshRenderer that SHARES the model's working mesh,
    /// bones, bindposes and rootBone — so it deforms pixel-for-pixel on top of the
    /// real avatar — but draws with an unlit, transparent vertex-colour material.
    /// Each vertex is tinted by the ACTIVE bone's weight using the classic
    /// blue(0) -> cyan -> green -> yellow -> red(1) ramp.
    ///
    /// Opacity is the overlay material's tint alpha, so the user can fade it down to
    /// see the model's real shaders/textures beneath. Toggling off restores the
    /// mesh's original vertex colours (if any) and removes the overlay renderer.
    /// </summary>
    public class WeightHeatmap
    {
        GameObject overlayGO;
        SkinnedMeshRenderer overlay;
        Material mat;

        SkinnedMeshRenderer baseRenderer;
        WeightModel model;
        Mesh mesh;                 // == model.workingMesh
        Color32[] colors;          // scratch buffer we write
        Color32[] savedColors;     // original mesh colours, restored on detach
        bool hadColors;

        public bool Enabled { get; private set; }
        float opacity = 0.75f;
        int activeBone = -1;

        /// <summary>Bind to a freshly (re)loaded model. Re-creates the overlay if enabled.</summary>
        public void Attach(SkinnedMeshRenderer renderer, WeightModel m)
        {
            bool wasEnabled = Enabled;
            Detach();                 // tear down anything pointed at the old mesh
            baseRenderer = renderer;
            model = m;
            mesh = (m != null) ? m.workingMesh : null;
            if (wasEnabled) SetEnabled(true);
        }

        public void SetActiveBone(int bone)
        {
            activeBone = bone;
            if (Enabled) Refresh();
        }

        public void SetOpacity(float o)
        {
            opacity = Mathf.Clamp01(o);
            if (mat != null) mat.color = new Color(1f, 1f, 1f, opacity);
        }

        public void SetEnabled(bool on)
        {
            if (on)
            {
                if (baseRenderer == null || mesh == null) { Enabled = false; return; }
                BuildOverlay();
                Enabled = overlay != null;
                if (Enabled) Refresh();
            }
            else
            {
                RestoreColors();
                DestroyOverlay();
                Enabled = false;
            }
        }

        void BuildOverlay()
        {
            if (overlay != null) return;

            // Remember the mesh's current vertex colours so we can put them back.
            savedColors = mesh.colors32;
            hadColors = savedColors != null && savedColors.Length == mesh.vertexCount;

            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            mat = new Material(sh) { name = "WeightStudio_HeatmapMat" };
            mat.color = new Color(1f, 1f, 1f, opacity);

            overlayGO = new GameObject("WeightStudio_Heatmap");
            overlayGO.transform.SetParent(baseRenderer.transform, false);
            overlayGO.transform.localPosition = Vector3.zero;
            overlayGO.transform.localRotation = Quaternion.identity;
            overlayGO.transform.localScale = Vector3.one;

            overlay = overlayGO.AddComponent<SkinnedMeshRenderer>();
            overlay.sharedMesh = mesh;
            overlay.bones = baseRenderer.bones;
            overlay.rootBone = baseRenderer.rootBone;
            overlay.quality = baseRenderer.quality;
            overlay.updateWhenOffscreen = true;
            overlay.localBounds = baseRenderer.localBounds;
            overlay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            overlay.receiveShadows = false;
            overlay.allowOcclusionWhenDynamic = false;

            int sub = Mathf.Max(1, mesh.subMeshCount);
            var mats = new Material[sub];
            for (int i = 0; i < sub; i++) mats[i] = mat;
            overlay.sharedMaterials = mats;
        }

        /// <summary>Recompute per-vertex colours from the active bone's weights.</summary>
        public void Refresh()
        {
            if (!Enabled || mesh == null || model == null) return;
            int vc = model.vertexCount;
            if (colors == null || colors.Length != vc) colors = new Color32[vc];

            for (int v = 0; v < vc; v++)
            {
                int w = model.weldOf[v];
                float wt = 0f;
                if (activeBone >= 0 && model.weldWeights[w].TryGetValue(activeBone, out float x)) wt = x;
                colors[v] = WeightToColor(wt);
            }
            mesh.colors32 = colors;
        }

        // Blender-style weight ramp: 0 = blue, .25 = cyan, .5 = green, .75 = yellow, 1 = red.
        public static Color32 WeightToColor(float w)
        {
            w = Mathf.Clamp01(w);
            float r, g, b;
            if (w < 0.25f) { float t = w / 0.25f; r = 0f; g = t; b = 1f; }
            else if (w < 0.5f) { float t = (w - 0.25f) / 0.25f; r = 0f; g = 1f; b = 1f - t; }
            else if (w < 0.75f) { float t = (w - 0.5f) / 0.25f; r = t; g = 1f; b = 0f; }
            else { float t = (w - 0.75f) / 0.25f; r = 1f; g = 1f - t; b = 0f; }
            return new Color32((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f), 255);
        }

        void RestoreColors()
        {
            if (mesh == null) return;
            if (hadColors && savedColors != null && savedColors.Length == mesh.vertexCount)
                mesh.colors32 = savedColors;
            else
                mesh.colors32 = null;
        }

        void DestroyOverlay()
        {
            if (overlayGO != null) Object.Destroy(overlayGO);
            if (mat != null) Object.Destroy(mat);
            overlayGO = null; overlay = null; mat = null;
        }

        public void Detach()
        {
            if (Enabled) RestoreColors();
            DestroyOverlay();
            // keep Enabled flag so Attach() can re-create on the new mesh
            baseRenderer = null; model = null; mesh = null;
            savedColors = null; hadColors = false;
        }
    }
}
