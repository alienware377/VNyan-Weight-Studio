using System;
using System.Collections.Generic;
using UnityEngine;

namespace VNyanWeightStudio
{
    public enum BrushMode
    {
        Add,        // raise the active bone's weight under the brush
        Subtract,   // lower the active bone's weight
        Replace,    // push the active bone's weight toward a target value
        Smooth,     // average the active bone's weight with neighbours
        Blur        // average ALL influences with neighbours (full Laplacian step)
    }

    [Serializable]
    public struct BrushConfig
    {
        public float radius;     // world-space metres
        public float strength;   // 0..1 per application
        public float falloff;    // 0 (hard) .. 1 (soft)
        public int activeBone;   // bone index being painted
        public float target;     // for Replace mode
        public BrushMode mode;

        public static BrushConfig Default => new BrushConfig
        {
            radius = 0.04f,
            strength = 0.5f,
            falloff = 0.6f,
            activeBone = 0,
            target = 1f,
            mode = BrushMode.Add
        };
    }

    [Serializable]
    public struct SmoothConfig
    {
        public float boundaryThreshold;
        public int smoothRadius;
        public int iterations;
        public float lambda;
        public int maxBonesPerVertex;
        public float minWeight;

        public static SmoothConfig Default => new SmoothConfig
        {
            boundaryThreshold = 0.03f,
            smoothRadius = 3,
            iterations = 4,
            lambda = 0.5f,
            maxBonesPerVertex = 8,
            minWeight = 0.0005f
        };
    }

    public struct StrokeResult
    {
        public int affectedWelds;
        public int maxBonesObserved;
    }

    /// <summary>
    /// Brush + smoothing operations over a <see cref="WeightModel"/>. All operate on
    /// the welded weight maps; the caller scatters to the mesh with ApplyToMesh().
    /// </summary>
    public static class WeightTools
    {
        // ---------------- brush ----------------

        /// <summary>
        /// Apply one brush dab centred at <paramref name="worldHit"/>. Requires the
        /// model's baked world positions to be current (call RefreshWorldPositions).
        /// </summary>
        public static StrokeResult ApplyBrush(WeightModel m, Vector3 worldHit, BrushConfig cfg)
        {
            var res = new StrokeResult();
            if (m == null || !m.Loaded || m.weldWorldPos == null) return res;
            if (cfg.activeBone < 0) return res;

            float r = Mathf.Max(1e-4f, cfg.radius);
            float r2 = r * r;
            float shape = Mathf.Lerp(0.3f, 3.0f, Mathf.Clamp01(cfg.falloff));

            // gather affected welds + falloff
            var hit = new List<int>();
            var fall = new List<float>();
            for (int w = 0; w < m.weldCount; w++)
            {
                Vector3 d = m.weldWorldPos[w] - worldHit;
                float sq = d.sqrMagnitude;
                if (sq > r2) continue;
                float t = 1f - Mathf.Sqrt(sq) / r;
                float f = Mathf.Pow(Mathf.Clamp01(t), shape);
                if (f <= 0f) continue;
                hit.Add(w);
                fall.Add(f);
            }
            if (hit.Count == 0) return res;

            // Blur needs a read-only snapshot of neighbours
            Dictionary<int, float>[] snapshot = null;
            if (cfg.mode == BrushMode.Blur || cfg.mode == BrushMode.Smooth)
                snapshot = m.weldWeights;

            int max = 0;
            for (int i = 0; i < hit.Count; i++)
            {
                int w = hit[i];
                float amt = Mathf.Clamp01(cfg.strength) * fall[i];
                var dict = m.weldWeights[w];

                switch (cfg.mode)
                {
                    case BrushMode.Add:
                    {
                        float cur = dict.TryGetValue(cfg.activeBone, out float c) ? c : 0f;
                        WeightModel.SetWeightNormalized(dict, cfg.activeBone, Mathf.Clamp01(cur + amt));
                        break;
                    }
                    case BrushMode.Subtract:
                    {
                        float cur = dict.TryGetValue(cfg.activeBone, out float c) ? c : 0f;
                        WeightModel.SetWeightNormalized(dict, cfg.activeBone, Mathf.Clamp01(cur - amt));
                        break;
                    }
                    case BrushMode.Replace:
                    {
                        float cur = dict.TryGetValue(cfg.activeBone, out float c) ? c : 0f;
                        float val = Mathf.Lerp(cur, Mathf.Clamp01(cfg.target), amt);
                        WeightModel.SetWeightNormalized(dict, cfg.activeBone, val);
                        break;
                    }
                    case BrushMode.Smooth:
                    {
                        float nbrAvg = NeighbourAverage(m, snapshot, w, cfg.activeBone);
                        float cur = dict.TryGetValue(cfg.activeBone, out float c) ? c : 0f;
                        float val = Mathf.Lerp(cur, nbrAvg, amt);
                        WeightModel.SetWeightNormalized(dict, cfg.activeBone, val);
                        break;
                    }
                    case BrushMode.Blur:
                    {
                        BlurWeld(m, snapshot, w, amt);
                        break;
                    }
                }

                if (dict.Count > max) max = dict.Count;
            }

            m.dirty = true;
            res.affectedWelds = hit.Count;
            res.maxBonesObserved = max;
            return res;
        }

        static float NeighbourAverage(WeightModel m, Dictionary<int, float>[] snap, int w, int bone)
        {
            var nbrs = m.neighbors[w];
            if (nbrs.Count == 0)
                return snap[w].TryGetValue(bone, out float s) ? s : 0f;
            float sum = 0f;
            foreach (int u in nbrs)
                sum += snap[u].TryGetValue(bone, out float uw) ? uw : 0f;
            return sum / nbrs.Count;
        }

        static void BlurWeld(WeightModel m, Dictionary<int, float>[] snap, int w, float amt)
        {
            var nbrs = m.neighbors[w];
            if (nbrs.Count == 0) return;

            var bones = new HashSet<int>(snap[w].Keys);
            foreach (int u in nbrs)
                foreach (int b in snap[u].Keys) bones.Add(b);

            var updated = new Dictionary<int, float>(bones.Count);
            foreach (int b in bones)
            {
                float cur = snap[w].TryGetValue(b, out float cw) ? cw : 0f;
                float ns = 0f;
                foreach (int u in nbrs) ns += snap[u].TryGetValue(b, out float uw) ? uw : 0f;
                ns /= nbrs.Count;
                float val = Mathf.Lerp(cur, ns, amt);
                if (val > 0f) updated[b] = val;
            }
            WeightModel.Normalize(updated);
            m.weldWeights[w] = updated;
        }

        // ---------------- bone-centric edits ----------------

        /// <summary>Remove a bone's influence everywhere and renormalize.</summary>
        public static int RemoveBoneInfluence(WeightModel m, int bone)
        {
            int changed = 0;
            for (int w = 0; w < m.weldCount; w++)
            {
                if (m.weldWeights[w].Remove(bone))
                {
                    if (m.weldWeights[w].Count == 0) m.weldWeights[w][0] = 1f;
                    WeightModel.Normalize(m.weldWeights[w]);
                    changed++;
                }
            }
            if (changed > 0) m.dirty = true;
            return changed;
        }

        /// <summary>Move all of <paramref name="from"/>'s weight onto <paramref name="to"/>.</summary>
        public static int TransferBone(WeightModel m, int from, int to)
        {
            int changed = 0;
            for (int w = 0; w < m.weldCount; w++)
            {
                var d = m.weldWeights[w];
                if (d.TryGetValue(from, out float fw) && fw > 0f)
                {
                    d[to] = d.TryGetValue(to, out float tw) ? tw + fw : fw;
                    d.Remove(from);
                    WeightModel.Normalize(d);
                    changed++;
                }
            }
            if (changed > 0) m.dirty = true;
            return changed;
        }

        /// <summary>Cap every vertex to its N strongest influences.</summary>
        public static void LimitInfluences(WeightModel m, int maxBones)
        {
            if (maxBones < 1) maxBones = 1;
            for (int w = 0; w < m.weldCount; w++)
            {
                var d = m.weldWeights[w];
                if (d.Count <= maxBones) continue;
                var ordered = new List<KeyValuePair<int, float>>(d);
                ordered.Sort((x, y) => y.Value.CompareTo(x.Value));
                d.Clear();
                for (int i = 0; i < maxBones; i++) d[ordered[i].Key] = ordered[i].Value;
                WeightModel.Normalize(d);
            }
            m.dirty = true;
        }

        // ---------------- whole-mesh smoothing (boundary-aware Laplacian) ----------------

        /// <summary>
        /// Diffuse choppy weight boundaries. If <paramref name="selectionMask"/> is
        /// non-null, only those welds (and their grown neighbourhood) are touched.
        /// Adapted from the original Bone Weight Smoother.
        /// </summary>
        public static StrokeResult SmoothMesh(WeightModel m, SmoothConfig cfg, bool[] selectionMask = null)
        {
            var res = new StrokeResult();
            if (m == null || !m.Loaded) return res;

            int weldCount = m.weldCount;
            var active = new bool[weldCount];
            var frontier = new List<int>();

            if (selectionMask != null)
            {
                for (int w = 0; w < weldCount; w++)
                    if (selectionMask[w]) { active[w] = true; frontier.Add(w); }
            }
            else
            {
                // detect hard boundaries: weight >= threshold next to a 0-weight neighbour
                for (int w = 0; w < weldCount; w++)
                {
                    bool boundary = false;
                    foreach (var kv in m.weldWeights[w])
                    {
                        if (kv.Value < cfg.boundaryThreshold) continue;
                        foreach (int u in m.neighbors[w])
                            if (!m.weldWeights[u].TryGetValue(kv.Key, out float uw) || uw <= 0f) { boundary = true; break; }
                        if (boundary) break;
                    }
                    if (boundary) { active[w] = true; frontier.Add(w); }
                }
            }
            res.affectedWelds = frontier.Count;
            if (frontier.Count == 0) return res;

            // grow the active region
            var current = frontier;
            for (int r = 0; r < cfg.smoothRadius; r++)
            {
                var next = new List<int>();
                foreach (int w in current)
                    foreach (int u in m.neighbors[w])
                        if (!active[u]) { active[u] = true; next.Add(u); }
                if (next.Count == 0) break;
                current = next;
            }

            float lambda = Mathf.Clamp01(cfg.lambda);
            var w2 = m.weldWeights;
            for (int iter = 0; iter < cfg.iterations; iter++)
            {
                var snap = w2;
                var nextArr = new Dictionary<int, float>[weldCount];
                for (int w = 0; w < weldCount; w++)
                {
                    if (!active[w]) { nextArr[w] = snap[w]; continue; }
                    var nbrs = m.neighbors[w];
                    var bones = new HashSet<int>(snap[w].Keys);
                    foreach (int u in nbrs)
                        foreach (int b in snap[u].Keys) bones.Add(b);

                    var upd = new Dictionary<int, float>(bones.Count);
                    foreach (int b in bones)
                    {
                        float cur = snap[w].TryGetValue(b, out float cw) ? cw : 0f;
                        float sum = 0f;
                        if (nbrs.Count > 0)
                        {
                            foreach (int u in nbrs) sum += snap[u].TryGetValue(b, out float uw) ? uw : 0f;
                            sum /= nbrs.Count;
                        }
                        float val = (1f - lambda) * cur + lambda * sum;
                        if (val > 0f) upd[b] = val;
                    }
                    WeightModel.Normalize(upd);
                    nextArr[w] = upd;
                }
                w2 = nextArr;
            }

            int maxObserved = 0;
            for (int w = 0; w < weldCount; w++)
            {
                if (active[w])
                {
                    WeightModel.Prune(w2[w], cfg.minWeight);
                    if (cfg.maxBonesPerVertex > 0 && w2[w].Count > cfg.maxBonesPerVertex)
                        LimitOne(w2[w], cfg.maxBonesPerVertex);
                    WeightModel.Normalize(w2[w]);
                }
                if (w2[w].Count > maxObserved) maxObserved = w2[w].Count;
            }
            m.weldWeights = w2;
            m.dirty = true;
            res.maxBonesObserved = maxObserved;
            return res;
        }

        /// <summary>
        /// Smooth ONLY the active <paramref name="bone"/>'s weight channel (like the
        /// brush's Smooth mode, but over the whole mesh or a selection at once).
        /// Each touched weld's <paramref name="bone"/> weight is iteratively pulled
        /// toward its welded-neighbour average, then the weld is renormalized so the
        /// other influences still sum to 1.
        /// If <paramref name="selectionMask"/> is non-null only those welds (grown by
        /// smoothRadius) are touched; otherwise the welds that carry this bone (and
        /// their boundary with welds that don't) define the active region.
        /// </summary>
        public static StrokeResult SmoothBone(WeightModel m, int bone, SmoothConfig cfg, bool[] selectionMask = null)
        {
            var res = new StrokeResult();
            if (m == null || !m.Loaded || bone < 0) return res;

            int weldCount = m.weldCount;
            var active = new bool[weldCount];
            var frontier = new List<int>();

            if (selectionMask != null)
            {
                for (int w = 0; w < weldCount; w++)
                    if (selectionMask[w]) { active[w] = true; frontier.Add(w); }
            }
            else
            {
                // seed with every weld that carries this bone OR neighbours one that does,
                // so the bone's whole influence region (and its boundary) is smoothed.
                for (int w = 0; w < weldCount; w++)
                {
                    bool seed = m.weldWeights[w].TryGetValue(bone, out float bw) && bw > 0f;
                    if (!seed)
                    {
                        foreach (int u in m.neighbors[w])
                            if (m.weldWeights[u].TryGetValue(bone, out float uw) && uw > 0f) { seed = true; break; }
                    }
                    if (seed) { active[w] = true; frontier.Add(w); }
                }
            }
            res.affectedWelds = frontier.Count;
            if (frontier.Count == 0) return res;

            // grow the active region
            var current = frontier;
            for (int r = 0; r < cfg.smoothRadius; r++)
            {
                var next = new List<int>();
                foreach (int w in current)
                    foreach (int u in m.neighbors[w])
                        if (!active[u]) { active[u] = true; next.Add(u); }
                if (next.Count == 0) break;
                current = next;
            }

            float lambda = Mathf.Clamp01(cfg.lambda);
            for (int iter = 0; iter < cfg.iterations; iter++)
            {
                var snap = m.weldWeights;
                // read this bone's value from the snapshot, write smoothed values in place
                var newVals = new float[weldCount];
                for (int w = 0; w < weldCount; w++)
                {
                    if (!active[w]) continue;
                    float avg = NeighbourAverage(m, snap, w, bone);
                    float cur = snap[w].TryGetValue(bone, out float c) ? c : 0f;
                    newVals[w] = (1f - lambda) * cur + lambda * avg;
                }
                for (int w = 0; w < weldCount; w++)
                {
                    if (!active[w]) continue;
                    WeightModel.SetWeightNormalized(m.weldWeights[w], bone, newVals[w]);
                }
            }

            int maxObserved = 0;
            for (int w = 0; w < weldCount; w++)
            {
                if (active[w])
                {
                    WeightModel.Prune(m.weldWeights[w], cfg.minWeight);
                    WeightModel.Normalize(m.weldWeights[w]);
                }
                if (m.weldWeights[w].Count > maxObserved) maxObserved = m.weldWeights[w].Count;
            }
            m.dirty = true;
            res.maxBonesObserved = maxObserved;
            return res;
        }

        static void LimitOne(Dictionary<int, float> d, int maxBones)
        {
            var ordered = new List<KeyValuePair<int, float>>(d);
            ordered.Sort((x, y) => y.Value.CompareTo(x.Value));
            d.Clear();
            for (int i = 0; i < maxBones; i++) d[ordered[i].Key] = ordered[i].Value;
        }
    }
}
