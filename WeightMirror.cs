using System;
using System.Collections.Generic;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Mirrors skin weights across an axis, two ways:
    ///  - bone to bone: copy the active bone's influence to its L/R counterpart,
    ///    sampling weights at each destination vertex's mirrored position
    ///  - self mirror: one bone's weights copied from one side of the mesh onto
    ///    the other side (same bone)
    ///
    /// Matching methods:
    ///  - exact: match in MESH LOCAL rest space (the model's default/bind pose,
    ///    i.e. the VRM default position). Snap-grid lookup with a tiny tolerance,
    ///    so true mirror-modelled meshes map 1:1.
    ///  - projection: match in avatar-root local space using the CURRENTLY POSED
    ///    (baked) vertex positions, blending the 3 nearest mirrored verts by
    ///    inverse distance. Pose must be still and symmetric for good results.
    ///
    /// mix: 0 = keep existing weights, 1 = fully take the mirrored value.
    /// All edits go through SetWeightNormalized so totals stay at 1.
    /// </summary>
    public static class WeightMirror
    {
        public const float ExactEps = 5e-4f;    // exact-match snap (0.5 mm)

        // ---- matcher built once per operation ----
        sealed class Matcher
        {
            public bool projection;
            public int axis;
            // exact: snap-grid over rest positions
            public Dictionary<Vector3Int, int> grid;
            public Vector3[] pos;                // positions used for matching (rest or root-local posed)

            public Vector3 Mirror(Vector3 p)
            {
                if (axis == 0) p.x = -p.x; else if (axis == 1) p.y = -p.y; else p.z = -p.z;
                return p;
            }

            // Find source welds for a mirrored point. Returns up to 3 (weld, weight) pairs.
            public int Sample(Vector3 mirrored, int[] outIdx, float[] outWgt)
            {
                if (!projection)
                {
                    var key = Key(mirrored);
                    int wid;
                    if (grid.TryGetValue(key, out wid)) { outIdx[0] = wid; outWgt[0] = 1f; return 1; }
                    // tolerate off-by-one cell (borderline rounding)
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                if (dx == 0 && dy == 0 && dz == 0) continue;
                                if (grid.TryGetValue(new Vector3Int(key.x + dx, key.y + dy, key.z + dz), out wid))
                                {
                                    if ((pos[wid] - mirrored).sqrMagnitude <= ExactEps * ExactEps)
                                    { outIdx[0] = wid; outWgt[0] = 1f; return 1; }
                                }
                            }
                    return 0;
                }

                // projection: 3 nearest, inverse-distance weights (brute force is fine
                // at typical vert counts; runs once per click)
                int n0 = -1, n1 = -1, n2 = -1;
                float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue;
                for (int i = 0; i < pos.Length; i++)
                {
                    float d = (pos[i] - mirrored).sqrMagnitude;
                    if (d < d0) { d2 = d1; n2 = n1; d1 = d0; n1 = n0; d0 = d; n0 = i; }
                    else if (d < d1) { d2 = d1; n2 = n1; d1 = d; n1 = i; }
                    else if (d < d2) { d2 = d; n2 = i; }
                }
                if (n0 < 0) return 0;
                float w0 = 1f / (Mathf.Sqrt(d0) + 1e-6f);
                float w1 = n1 >= 0 ? 1f / (Mathf.Sqrt(d1) + 1e-6f) : 0f;
                float w2 = n2 >= 0 ? 1f / (Mathf.Sqrt(d2) + 1e-6f) : 0f;
                float sum = w0 + w1 + w2;
                int cnt = 0;
                outIdx[cnt] = n0; outWgt[cnt++] = w0 / sum;
                if (n1 >= 0) { outIdx[cnt] = n1; outWgt[cnt++] = w1 / sum; }
                if (n2 >= 0) { outIdx[cnt] = n2; outWgt[cnt++] = w2 / sum; }
                return cnt;
            }

            static Vector3Int Key(Vector3 p)
            {
                float inv = 1f / ExactEps;
                return new Vector3Int(Mathf.RoundToInt(p.x * inv), Mathf.RoundToInt(p.y * inv), Mathf.RoundToInt(p.z * inv));
            }
        }

        static Matcher Build(WeightModel model, bool projection, int axis, Transform root)
        {
            var m = new Matcher { projection = projection, axis = axis };
            if (!projection)
            {
                m.pos = model.weldRestPos;
                m.grid = new Dictionary<Vector3Int, int>(model.weldCount);
                float inv = 1f / ExactEps;
                for (int w = 0; w < model.weldCount; w++)
                {
                    Vector3 p = model.weldRestPos[w];
                    var key = new Vector3Int(Mathf.RoundToInt(p.x * inv), Mathf.RoundToInt(p.y * inv), Mathf.RoundToInt(p.z * inv));
                    if (!m.grid.ContainsKey(key)) m.grid[key] = w;
                }
            }
            else
            {
                model.RefreshWorldPositions(true);
                m.pos = new Vector3[model.weldCount];
                Matrix4x4 w2r = root != null ? root.worldToLocalMatrix : Matrix4x4.identity;
                for (int w = 0; w < model.weldCount; w++)
                    m.pos[w] = w2r.MultiplyPoint3x4(model.weldWorldPos[w]);
            }
            return m;
        }

        static float GetW(Dictionary<int, float> d, int bone)
        {
            float v;
            return d.TryGetValue(bone, out v) ? v : 0f;
        }

        /// <summary>
        /// Copy srcBone's influence, mirrored, onto dstBone. Touches every weld whose
        /// mirrored sample carries srcBone weight, plus welds that already have dstBone
        /// weight (so mix=1 fully replaces). Returns changed weld count.
        /// </summary>
        public static int MirrorBoneToBone(WeightModel model, int srcBone, int dstBone,
            int axis, bool projection, float mix, Transform root, out int unmatched)
        {
            var mt = Build(model, projection, axis, root);
            var idx = new int[3];
            var wgt = new float[3];
            int changed = 0;
            unmatched = 0;

            for (int w = 0; w < model.weldCount; w++)
            {
                int n = mt.Sample(mt.Mirror(mt.pos[w]), idx, wgt);
                float target;
                if (n == 0)
                {
                    if (GetW(model.weldWeights[w], dstBone) <= 0f) continue;
                    unmatched++;
                    continue;   // no mirror source found: leave this vert alone
                }
                else
                {
                    target = 0f;
                    for (int k = 0; k < n; k++) target += wgt[k] * GetW(model.weldWeights[idx[k]], srcBone);
                }

                float cur = GetW(model.weldWeights[w], dstBone);
                float nv = Mathf.Lerp(cur, target, mix);
                if (Mathf.Abs(nv - cur) < 1e-5f) continue;
                WeightModel.SetWeightNormalized(model.weldWeights[w], dstBone, nv);
                changed++;
            }
            if (changed > 0) model.dirty = true;
            return changed;
        }

        /// <summary>
        /// Mirror one bone's weights from the source side of the mesh (sign of the
        /// axis coordinate == sourceSign) onto the opposite side. Verts within the
        /// tolerance band around the mirror plane are left untouched.
        /// </summary>
        public static int MirrorSelf(WeightModel model, int bone, int axis, int sourceSign,
            bool projection, float mix, Transform root, out int unmatched)
        {
            var mt = Build(model, projection, axis, root);
            var idx = new int[3];
            var wgt = new float[3];
            int changed = 0;
            unmatched = 0;

            for (int w = 0; w < model.weldCount; w++)
            {
                float c = axis == 0 ? mt.pos[w].x : axis == 1 ? mt.pos[w].y : mt.pos[w].z;
                if (Mathf.Abs(c) <= ExactEps) continue;            // on the plane
                if (Math.Sign(c) == sourceSign) continue;          // source side: untouched

                int n = mt.Sample(mt.Mirror(mt.pos[w]), idx, wgt);
                if (n == 0)
                {
                    unmatched++;
                    continue;
                }
                float target = 0f;
                for (int k = 0; k < n; k++) target += wgt[k] * GetW(model.weldWeights[idx[k]], bone);

                float cur = GetW(model.weldWeights[w], bone);
                float nv = Mathf.Lerp(cur, target, mix);
                if (Mathf.Abs(nv - cur) < 1e-5f) continue;
                WeightModel.SetWeightNormalized(model.weldWeights[w], bone, nv);
                changed++;
            }
            if (changed > 0) model.dirty = true;
            return changed;
        }

        /// <summary>Find the L/R counterpart bone by name, or -1.</summary>
        public static int FindCounterpart(string[] boneNames, int bone)
        {
            if (boneNames == null || bone < 0 || bone >= boneNames.Length) return -1;
            string nm = boneNames[bone];
            string sw = SwapLR(nm);
            if (sw == null || sw == nm) return -1;
            for (int i = 0; i < boneNames.Length; i++)
                if (i != bone && string.Equals(boneNames[i], sw, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static readonly string[,] PAIRS =
        {
            { "_L", "_R" }, { "_R", "_L" }, { ".L", ".R" }, { ".R", ".L" },
            { "_l", "_r" }, { "_r", "_l" }, { ".l", ".r" }, { ".r", ".l" },
            { "Left", "Right" }, { "Right", "Left" }, { "left", "right" }, { "right", "left" },
            { "L_", "R_" }, { "R_", "L_" }, { " L", " R" }, { " R", " L" },
        };

        static string SwapLR(string nm)
        {
            // suffixes first (most common in VRM/VRChat rigs)
            for (int i = 0; i < PAIRS.GetLength(0); i++)
            {
                string a = PAIRS[i, 0], b = PAIRS[i, 1];
                if (a.EndsWith("_") || a.EndsWith(" "))   // prefix pattern
                {
                    if (nm.StartsWith(a)) return b + nm.Substring(a.Length);
                }
                else if (nm.EndsWith(a)) return nm.Substring(0, nm.Length - a.Length) + b;
            }
            // embedded Left/Right words anywhere
            if (nm.Contains("Left")) return nm.Replace("Left", "Right");
            if (nm.Contains("Right")) return nm.Replace("Right", "Left");
            if (nm.Contains("left")) return nm.Replace("left", "right");
            if (nm.Contains("right")) return nm.Replace("right", "left");
            return null;
        }
    }
}
