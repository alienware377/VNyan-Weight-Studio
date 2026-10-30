using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Shape-key weights as VNyan actually RENDERS them.
    ///
    /// VNyan zeroes an avatar's shape keys early in every frame and sets them again late,
    /// after the plugins' LateUpdate (measured on a test avatar: a garment's shape key reads 0 in
    /// Update and LateUpdate, and its real value from onPreCull to the end of the frame). Anything reading
    /// or baking shape keys from Update/LateUpdate/a button click sees the bare shape: the
    /// heatmap overlay copied zeros and sat inside the visible garment, the brush aimed at a
    /// smaller surface than the one on screen, and Weight Transfer sampled the wrong positions.
    /// So the weights of the renderers Weight Studio works on are captured at the END of the
    /// frame, and every bake goes through <see cref="Bake"/>, which uses them.
    /// </summary>
    public static class LiveShapes
    {
        static readonly Dictionary<SkinnedMeshRenderer, float[]> captured = new Dictionary<SkinnedMeshRenderer, float[]>();

        /// <summary>Records the renderer's current weights (call at end of frame).</summary>
        public static float[] Capture(SkinnedMeshRenderer r)
        {
            Mesh m = r != null ? r.sharedMesh : null;
            if (m == null) return null;
            int n = m.blendShapeCount;
            float[] w;
            if (!captured.TryGetValue(r, out w) || w.Length != n) { w = new float[n]; captured[r] = w; }
            for (int i = 0; i < n; i++) w[i] = r.GetBlendShapeWeight(i);
            return w;
        }

        public static float[] Get(SkinnedMeshRenderer r)
        {
            float[] w;
            if (r == null || !captured.TryGetValue(r, out w)) return null;
            return r.sharedMesh != null && w.Length == r.sharedMesh.blendShapeCount ? w : null;
        }

        /// <summary>Keeps only the renderers still being watched (drops destroyed ones).</summary>
        public static void Retain(ICollection<SkinnedMeshRenderer> keep)
        {
            var drop = new List<SkinnedMeshRenderer>();
            foreach (var kv in captured) if (kv.Key == null || !keep.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (var k in drop) captured.Remove(k);
        }

        /// <summary>
        /// BakeMesh with the shape keys as last rendered, then VNyan's in-frame values are put
        /// back untouched (it overwrites them later in the frame anyway).
        /// </summary>
        public static void Bake(SkinnedMeshRenderer r, Mesh into)
        {
            float[] live = Get(r);
            if (live == null) { r.BakeMesh(into, true); return; }
            int n = live.Length;
            float[] keep = new float[n];
            bool differs = false;
            for (int i = 0; i < n; i++) { keep[i] = r.GetBlendShapeWeight(i); if (keep[i] != live[i]) differs = true; }
            if (!differs) { r.BakeMesh(into, true); return; }
            for (int i = 0; i < n; i++) if (keep[i] != live[i]) r.SetBlendShapeWeight(i, live[i]);
            try { r.BakeMesh(into, true); }
            finally { for (int i = 0; i < n; i++) if (keep[i] != live[i]) r.SetBlendShapeWeight(i, keep[i]); }
        }
    }

    public partial class BoneWeightStudioPlugin
    {
        bool shapeCaptureRunning;
        readonly List<SkinnedMeshRenderer> shapeWatch = new List<SkinnedMeshRenderer>();

        void ShapeCaptureTick()
        {
            if (shapeCaptureRunning || !isActiveAndEnabled) return;
            shapeCaptureRunning = true;
            StartCoroutine(ShapeKeyCapture());
        }

        void OnDisable() { shapeCaptureRunning = false; }   // coroutines die with the component

        IEnumerator ShapeKeyCapture()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                try { CaptureLiveShapes(); }
                catch (System.Exception e) { Debug.LogWarning("[WeightStudio] shape key capture: " + e.Message); }
            }
        }

        void CaptureLiveShapes()
        {
            shapeWatch.Clear();
            if (activeRenderer != null) shapeWatch.Add(activeRenderer);
            if (transferSource != null && transferSource != activeRenderer) shapeWatch.Add(transferSource);
            LiveShapes.Retain(shapeWatch);
            float[] active = null;
            foreach (SkinnedMeshRenderer r in shapeWatch)
            {
                float[] w = LiveShapes.Capture(r);
                if (r == activeRenderer) active = w;
            }
            if (heatmap.Enabled && active != null) heatmap.SyncBlendShapes(active);
        }
    }
}
