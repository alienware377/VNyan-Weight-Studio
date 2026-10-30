using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace VNyanWeightStudio
{
    public enum TransferMapping
    {
        NearestVertex,   // copy the single closest source vertex's weights
        NearestSmooth,   // inverse-distance average of the N nearest source vertices
        NearestSurface   // barycentric blend from the closest point on the source surface
    }

    [Serializable]
    public struct TransferConfig
    {
        public TransferMapping mapping;
        public int samples;        // N for NearestSmooth
        public float maxDistance;  // metres; 0 = unlimited
        public float blend;        // 0..1, how much of the sampled weights to apply (1 = full replace)
        public bool matchByName;   // map source->target bones by name (else by index)
        public bool selectedOnly;  // only welds in the selection mask
        public bool normalize;     // renormalize each target vertex afterward
        public bool worldSpace;    // compare positions in world (posed) space vs raw mesh-local

        public static TransferConfig Default => new TransferConfig
        {
            mapping = TransferMapping.NearestSmooth,
            samples = 4,
            maxDistance = 0f,
            blend = 1f,
            matchByName = true,
            selectedOnly = false,
            normalize = true,
            worldSpace = true
        };
    }

    public struct TransferResult
    {
        public int targetWelds;     // welds considered
        public int changedWelds;    // welds that received weights
        public int skippedNoSource; // welds with no source in range
        public int maxBonesObserved;
        public string message;
    }

    /// <summary>
    /// Blender-"Data Transfer"-style skin-weight copy between two meshes, by spatial
    /// proximity. Bones are matched by NAME (so two different rigs share weights as
    /// long as bone names line up), with a fallback to raw index. Supports nearest
    /// vertex, smoothed N-nearest averaging, and nearest-surface barycentric sampling.
    /// </summary>
    public static class WeightTransfer
    {
        // ---- source sampler ------------------------------------------------

        class Source
        {
            public Vector3[] pos;                 // per source vertex (world or local)
            public Dictionary<string, float>[] nameW; // per source vertex, boneName->weight
            public int[] tri;                     // flattened triangle vertex indices
            public List<int>[] vertTris;          // vertex -> list of triangle base indices (/3 groups)

            // spatial grid
            Dictionary<Vector3Int, List<int>> grid;
            float cell;
            Vector3 gmin;

            public static Source Build(SkinnedMeshRenderer smr, bool worldSpace)
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null || !mesh.isReadable) return null;
                int vc = mesh.vertexCount;

                var s = new Source();

                // positions
                if (worldSpace)
                {
                    Mesh baked = new Mesh();
                    LiveShapes.Bake(smr, baked);   // shape keys as rendered
                    Vector3[] bp = baked.vertices;
                    Matrix4x4 l2w = smr.transform.localToWorldMatrix;
                    s.pos = new Vector3[vc];
                    int n = Mathf.Min(vc, bp.Length);
                    for (int i = 0; i < n; i++) s.pos[i] = l2w.MultiplyPoint3x4(bp[i]);
                    UnityEngine.Object.Destroy(baked);
                }
                else
                {
                    s.pos = mesh.vertices;
                }

                // bone names
                Transform[] bones = smr.bones;
                string[] bn = new string[bones != null ? bones.Length : 0];
                for (int i = 0; i < bn.Length; i++) bn[i] = bones[i] != null ? bones[i].name : ("<missing " + i + ">");

                // weights -> name space
                byte[] bpv; BoneWeight1[] flat;
                {
                    var bpvView = mesh.GetBonesPerVertex();
                    var bwView = mesh.GetAllBoneWeights();
                    bpv = bpvView.Length > 0 ? bpvView.ToArray() : new byte[vc];
                    flat = bwView.Length > 0 ? bwView.ToArray() : new BoneWeight1[0];
                }
                s.nameW = new Dictionary<string, float>[vc];
                int off = 0;
                for (int v = 0; v < vc; v++)
                {
                    int cnt = v < bpv.Length ? bpv[v] : 0;
                    var d = new Dictionary<string, float>(Math.Max(1, cnt));
                    for (int k = 0; k < cnt; k++)
                    {
                        BoneWeight1 bw = flat[off + k];
                        if (bw.weight <= 0f) continue;
                        string name = (bw.boneIndex >= 0 && bw.boneIndex < bn.Length) ? bn[bw.boneIndex] : null;
                        if (name == null) continue;
                        d[name] = d.TryGetValue(name, out float ex) ? ex + bw.weight : bw.weight;
                    }
                    s.nameW[v] = d;
                    off += cnt;
                }

                // triangles + adjacency (for surface sampling)
                var triList = new List<int>(mesh.triangles.Length);
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    triList.AddRange(mesh.GetTriangles(sm));
                s.tri = triList.ToArray();
                s.vertTris = new List<int>[vc];
                for (int t = 0; t + 2 < s.tri.Length; t += 3)
                {
                    AddTri(s.vertTris, s.tri[t], t);
                    AddTri(s.vertTris, s.tri[t + 1], t);
                    AddTri(s.vertTris, s.tri[t + 2], t);
                }

                s.BuildGrid();
                return s;
            }

            static void AddTri(List<int>[] arr, int v, int t)
            {
                if (v < 0 || v >= arr.Length) return;
                if (arr[v] == null) arr[v] = new List<int>(4);
                arr[v].Add(t);
            }

            void BuildGrid()
            {
                int n = pos.Length;
                Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < n; i++)
                {
                    mn = Vector3.Min(mn, pos[i]);
                    mx = Vector3.Max(mx, pos[i]);
                }
                gmin = mn;
                float diag = (mx - mn).magnitude;
                // aim for ~1-2 verts per cell
                cell = Mathf.Max(diag / Mathf.Max(1f, Mathf.Pow(n, 1f / 3f)), 1e-4f);

                grid = new Dictionary<Vector3Int, List<int>>(n);
                for (int i = 0; i < n; i++)
                {
                    Vector3Int c = CellOf(pos[i]);
                    if (!grid.TryGetValue(c, out var list)) { list = new List<int>(2); grid[c] = list; }
                    list.Add(i);
                }
            }

            Vector3Int CellOf(Vector3 p)
            {
                return new Vector3Int(
                    Mathf.FloorToInt((p.x - gmin.x) / cell),
                    Mathf.FloorToInt((p.y - gmin.y) / cell),
                    Mathf.FloorToInt((p.z - gmin.z) / cell));
            }

            /// <summary>Collect up to k nearest source vertices to p, within maxDist (0 = inf).</summary>
            public void KNN(Vector3 p, int k, float maxDist, List<int> outIdx, List<float> outD2)
            {
                outIdx.Clear(); outD2.Clear();
                if (grid == null || pos.Length == 0) return;
                float maxD2 = maxDist > 0f ? maxDist * maxDist : float.MaxValue;
                Vector3Int center = CellOf(p);
                const int ringCap = 96;

                for (int r = 0; r <= ringCap; r++)
                {
                    // cells at chebyshev radius r
                    for (int dx = -r; dx <= r; dx++)
                        for (int dy = -r; dy <= r; dy++)
                            for (int dz = -r; dz <= r; dz++)
                            {
                                if (Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz))) != r) continue;
                                var key = new Vector3Int(center.x + dx, center.y + dy, center.z + dz);
                                if (!grid.TryGetValue(key, out var list)) continue;
                                for (int li = 0; li < list.Count; li++)
                                {
                                    int idx = list[li];
                                    float d2 = (pos[idx] - p).sqrMagnitude;
                                    if (d2 > maxD2) continue;
                                    InsertSorted(outIdx, outD2, idx, d2, k);
                                }
                            }

                    // can any unsearched cell beat our current worst? Nearest point in
                    // ring r+1 is at least r*cell away.
                    if (outIdx.Count >= k)
                    {
                        float worst = outD2[outIdx.Count - 1];
                        float lower = (float)r * cell;
                        if (lower * lower > worst) break;
                    }
                }
            }

            static void InsertSorted(List<int> idx, List<float> d2, int newIdx, float newD2, int k)
            {
                // keep ascending by distance, capped at k
                int pos = d2.Count;
                while (pos > 0 && d2[pos - 1] > newD2) pos--;
                if (pos >= k && k > 0) return; // worse than the k we already have
                idx.Insert(pos, newIdx);
                d2.Insert(pos, newD2);
                if (k > 0 && idx.Count > k) { idx.RemoveAt(idx.Count - 1); d2.RemoveAt(d2.Count - 1); }
            }
        }

        // ---- public entry --------------------------------------------------

        public static TransferResult Transfer(WeightModel target, SkinnedMeshRenderer sourceRenderer,
                                               TransferConfig cfg, bool[] selectionMask)
        {
            var res = new TransferResult();
            if (target == null || !target.Loaded) { res.message = "No target mesh loaded."; return res; }
            if (sourceRenderer == null || sourceRenderer.sharedMesh == null) { res.message = "No source mesh set."; return res; }
            if (sourceRenderer == target.smr) { res.message = "Source and target are the same mesh."; return res; }

            Source src = Source.Build(sourceRenderer, cfg.worldSpace);
            if (src == null) { res.message = "Source mesh is not readable."; return res; }

            // target sample positions
            Vector3[] tpos;
            if (cfg.worldSpace) { target.RefreshWorldPositions(true); tpos = target.weldWorldPos; }
            else tpos = target.weldRestPos;
            if (tpos == null) { res.message = "Could not resolve target positions."; return res; }

            // name -> target bone index
            var nameToIdx = new Dictionary<string, int>(target.boneNames.Length);
            for (int i = 0; i < target.boneNames.Length; i++)
                if (!nameToIdx.ContainsKey(target.boneNames[i])) nameToIdx[target.boneNames[i]] = i;

            int k = cfg.mapping == TransferMapping.NearestVertex ? 1 :
                    cfg.mapping == TransferMapping.NearestSurface ? Mathf.Max(4, cfg.samples) :
                    Mathf.Max(1, cfg.samples);

            var knnIdx = new List<int>(k + 1);
            var knnD2 = new List<float>(k + 1);
            float blend = Mathf.Clamp01(cfg.blend);
            int maxObserved = 0;

            for (int w = 0; w < target.weldCount; w++)
            {
                res.targetWelds++;
                if (cfg.selectedOnly && (selectionMask == null || w >= selectionMask.Length || !selectionMask[w]))
                    continue;

                Vector3 p = tpos[w];
                src.KNN(p, k, cfg.maxDistance, knnIdx, knnD2);
                if (knnIdx.Count == 0) { res.skippedNoSource++; continue; }

                // sample weights in NAME space
                Dictionary<string, float> sampled;
                if (cfg.mapping == TransferMapping.NearestSurface)
                    sampled = SampleSurface(src, p, knnIdx);
                else if (cfg.mapping == TransferMapping.NearestVertex)
                    sampled = new Dictionary<string, float>(src.nameW[knnIdx[0]]);
                else
                    sampled = SampleSmooth(src, knnIdx, knnD2);

                if (sampled == null || sampled.Count == 0) { res.skippedNoSource++; continue; }

                // name -> target index
                var proposed = new Dictionary<int, float>(sampled.Count);
                foreach (var kv in sampled)
                {
                    int bi;
                    if (cfg.matchByName)
                    {
                        if (!nameToIdx.TryGetValue(kv.Key, out bi)) continue; // unmatched bone dropped
                    }
                    else
                    {
                        // by-index fallback: source provided names; recover index isn't possible,
                        // so by-index mode reuses the name map when available, else skips.
                        if (!nameToIdx.TryGetValue(kv.Key, out bi)) continue;
                    }
                    proposed[bi] = proposed.TryGetValue(bi, out float ex) ? ex + kv.Value : kv.Value;
                }
                if (proposed.Count == 0) { res.skippedNoSource++; continue; }
                WeightModel.Normalize(proposed);

                // mix with existing
                Dictionary<int, float> result;
                if (blend >= 0.999f)
                {
                    result = proposed;
                }
                else
                {
                    result = new Dictionary<int, float>(target.weldWeights[w]);
                    var bones = new HashSet<int>(result.Keys);
                    foreach (int b in proposed.Keys) bones.Add(b);
                    var mixed = new Dictionary<int, float>(bones.Count);
                    foreach (int b in bones)
                    {
                        float cur = result.TryGetValue(b, out float c) ? c : 0f;
                        float prop = proposed.TryGetValue(b, out float pw) ? pw : 0f;
                        float val = (1f - blend) * cur + blend * prop;
                        if (val > 0f) mixed[b] = val;
                    }
                    result = mixed;
                }

                if (result.Count == 0) result[0] = 1f;
                if (cfg.normalize) WeightModel.Normalize(result);
                target.weldWeights[w] = result;
                res.changedWelds++;
                if (result.Count > maxObserved) maxObserved = result.Count;
            }

            target.dirty = res.changedWelds > 0;
            res.maxBonesObserved = maxObserved;
            res.message = "Transferred to " + res.changedWelds + " verts (" +
                          res.skippedNoSource + " out of range), up to " + maxObserved + " bones/vertex.";
            return res;
        }

        static Dictionary<string, float> SampleSmooth(Source src, List<int> idx, List<float> d2)
        {
            var acc = new Dictionary<string, float>();
            float total = 0f;
            for (int i = 0; i < idx.Count; i++)
            {
                float wgt = 1f / Mathf.Max(d2[i], 1e-8f); // inverse-distance-squared
                total += wgt;
                foreach (var kv in src.nameW[idx[i]])
                    acc[kv.Key] = acc.TryGetValue(kv.Key, out float ex) ? ex + kv.Value * wgt : kv.Value * wgt;
            }
            if (total > 0f)
            {
                var keys = new List<string>(acc.Keys);
                foreach (var key in keys) acc[key] /= total;
            }
            return acc;
        }

        static Dictionary<string, float> SampleSurface(Source src, Vector3 p, List<int> candVerts)
        {
            // examine triangles incident to the candidate vertices; keep the closest point.
            float bestD2 = float.MaxValue;
            int bestTri = -1; Vector3 bestPt = Vector3.zero;
            var seen = new HashSet<int>();
            for (int c = 0; c < candVerts.Count; c++)
            {
                var tris = src.vertTris[candVerts[c]];
                if (tris == null) continue;
                for (int ti = 0; ti < tris.Count; ti++)
                {
                    int t = tris[ti];
                    if (!seen.Add(t)) continue;
                    Vector3 a = src.pos[src.tri[t]];
                    Vector3 b = src.pos[src.tri[t + 1]];
                    Vector3 cc = src.pos[src.tri[t + 2]];
                    Vector3 cp = ClosestPointOnTriangle(p, a, b, cc);
                    float d2 = (cp - p).sqrMagnitude;
                    if (d2 < bestD2) { bestD2 = d2; bestTri = t; bestPt = cp; }
                }
            }
            if (bestTri < 0)
            {
                // fall back to nearest vertex
                return candVerts.Count > 0 ? new Dictionary<string, float>(src.nameW[candVerts[0]]) : null;
            }

            int i0 = src.tri[bestTri], i1 = src.tri[bestTri + 1], i2 = src.tri[bestTri + 2];
            Vector3 bary = Barycentric(bestPt, src.pos[i0], src.pos[i1], src.pos[i2]);
            var acc = new Dictionary<string, float>();
            Accumulate(acc, src.nameW[i0], bary.x);
            Accumulate(acc, src.nameW[i1], bary.y);
            Accumulate(acc, src.nameW[i2], bary.z);
            return acc;
        }

        static void Accumulate(Dictionary<string, float> acc, Dictionary<string, float> w, float scale)
        {
            if (scale <= 0f) return;
            foreach (var kv in w)
                acc[kv.Key] = acc.TryGetValue(kv.Key, out float ex) ? ex + kv.Value * scale : kv.Value * scale;
        }

        static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float v = d1 / (d1 - d3); return a + v * ab; }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float w = d2 / (d2 - d6); return a + w * ac; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return b + w * (c - b); }
            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            return a + ab * vv + ac * ww;
        }

        static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-12f) return new Vector3(1f, 0f, 0f);
            float v = (d11 * d20 - d01 * d21) / denom;
            float w = (d00 * d21 - d01 * d20) / denom;
            float u = 1f - v - w;
            return new Vector3(u, v, w);
        }
    }
}
