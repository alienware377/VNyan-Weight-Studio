using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Shape-key weights as VNyan renders them.
    ///
    /// VNyan zeroes an avatar's shape keys early in every frame and sets them again after the
    /// plugins' LateUpdate: in Update and LateUpdate a shape key reads 0, and from onPreCull to the
    /// end of the frame it has its real value. Code that reads or bakes shape keys from Update,
    /// LateUpdate or a button click therefore sees the unmorphed mesh. To match what is on screen,
    /// the weights of the renderers Weight Studio works on are captured at the end of the frame,
    /// and every bake goes through <see cref="Bake"/>, which uses them.
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
