using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Editable, weld-aware skin-weight model for one SkinnedMeshRenderer.
    ///
    /// The authoritative editing structure is <see cref="weldWeights"/>: one
    /// sparse boneIndex->weight map per *welded* vertex group (vertices that share
    /// a position are welded so seams paint/smooth as one). All brush and smoothing
    /// tools mutate weldWeights; <see cref="ApplyToMesh"/> scatters the result back
    /// to every original vertex and writes it with <c>SetBoneWeights</c>, which
    /// supports an unlimited number of bones per vertex (unlike the legacy
    /// 4-influence BoneWeight struct).
    ///
    /// Pure Unity (no VNyan dependency), so it can be reused or unit-tested.
    /// </summary>
    public class WeightModel
    {
        public SkinnedMeshRenderer smr;
        public Mesh workingMesh;          // editable clone we own and write to
        public Mesh sourceMesh;           // the renderer's mesh when we loaded
        public int vertexCount;

        public Transform[] bones;         // smr.bones
        public string[] boneNames;        // bones[i].name (for the UI)

        // ---- weld topology ----
        public int weldCount;
        public int[] weldOf;                       // original vertex -> weld id
        public List<int>[] weldMembers;            // weld id -> original verts
        public HashSet<int>[] neighbors;           // welded adjacency (from tris)
        public Dictionary<int, float>[] weldWeights; // weld id -> {bone: weight}
        public Vector3[] weldRestPos;              // weld id -> rest centroid (mesh local)

        // ---- baked deformed positions, world space, per weld (for brush picking) ----
        public Vector3[] weldWorldPos;
        Mesh _bakeTmp;
        float _lastBakeTime = -999f;

        public bool dirty;   // weldWeights changed since last ApplyToMesh

        // ---- undo / redo (snapshots of weldWeights) ----
        readonly List<Dictionary<int, float>[]> _undo = new List<Dictionary<int, float>[]>();
        readonly List<Dictionary<int, float>[]> _redo = new List<Dictionary<int, float>[]>();
        const int MaxUndo = 24;

        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }
        public int UndoDepth { get { return _undo.Count; } }
        public int RedoDepth { get { return _redo.Count; } }

        public bool Loaded { get { return smr != null && weldWeights != null; } }

        /// <summary>Build the model from a renderer's current shared mesh.</summary>
        public static WeightModel Load(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null) return null;
            Mesh src = renderer.sharedMesh;
            if (!src.isReadable)
            {
                Debug.LogError("[WeightStudio] Mesh '" + src.name + "' is not CPU-readable; cannot edit.");
                return null;
            }

            var m = new WeightModel();
            m.smr = renderer;
            m.sourceMesh = src;
            m.vertexCount = src.vertexCount;
            m.bones = renderer.bones;
            m.boneNames = new string[m.bones != null ? m.bones.Length : 0];
            for (int i = 0; i < m.boneNames.Length; i++)
                m.boneNames[i] = m.bones[i] != null ? m.bones[i].name : ("<missing " + i + ">");

            // --- read existing weights (NativeArray views; copy out, don't dispose) ---
            byte[] bpv;
            BoneWeight1[] flat;
            {
                var bpvView = src.GetBonesPerVertex();
                var bwView = src.GetAllBoneWeights();
                bpv = bpvView.Length > 0 ? bpvView.ToArray() : new byte[m.vertexCount];
                flat = bwView.Length > 0 ? bwView.ToArray() : new BoneWeight1[0];
            }

            var vertW = new Dictionary<int, float>[m.vertexCount];
            {
                int off = 0;
                for (int v = 0; v < m.vertexCount; v++)
                {
                    int n = v < bpv.Length ? bpv[v] : 0;
                    var d = new Dictionary<int, float>(Math.Max(1, n));
                    for (int k = 0; k < n; k++)
                    {
                        BoneWeight1 bw = flat[off + k];
                        if (bw.weight > 0f)
                            d[bw.boneIndex] = d.TryGetValue(bw.boneIndex, out float ex) ? ex + bw.weight : bw.weight;
                    }
                    if (d.Count == 0) d[0] = 1f; // unbound vert -> bind to root
                    vertW[v] = d;
                    off += n;
                }
            }

            Vector3[] pos = src.vertices;
            m.BuildWeld(pos, vertW, src);

            // Editable clone we own; never mutate the source asset.
            m.workingMesh = UnityEngine.Object.Instantiate(src);
            m.workingMesh.name = src.name + " (WeightStudio)";
            m.smr.sharedMesh = m.workingMesh;
            m.sourceMesh = m.workingMesh;

            m.ApplyToMesh(); // normalize + push initial state (no-op visually)
            m.dirty = false;
            return m;
        }

        void BuildWeld(Vector3[] pos, Dictionary<int, float>[] vertW, Mesh src)
        {
            float eps = 1e-5f;
            float invEps = 1f / Mathf.Max(eps, 1e-7f);
            weldOf = new int[vertexCount];
            var lookup = new Dictionary<Vector3Int, int>(vertexCount);
            weldMembers = new List<int>[0];
            var members = new List<List<int>>();
            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 p = pos[v];
                var key = new Vector3Int(
                    Mathf.RoundToInt(p.x * invEps),
                    Mathf.RoundToInt(p.y * invEps),
                    Mathf.RoundToInt(p.z * invEps));
                if (!lookup.TryGetValue(key, out int wid))
                {
                    wid = members.Count;
                    lookup[key] = wid;
                    members.Add(new List<int>());
                }
                weldOf[v] = wid;
                members[wid].Add(v);
            }
            weldCount = members.Count;
            weldMembers = members.ToArray();

            // aggregate weights per weld (average of members)
            weldWeights = new Dictionary<int, float>[weldCount];
            weldRestPos = new Vector3[weldCount];
            for (int w = 0; w < weldCount; w++)
            {
                var mem = weldMembers[w];
                var acc = new Dictionary<int, float>();
                Vector3 c = Vector3.zero;
                foreach (int vi in mem)
                {
                    c += pos[vi];
                    foreach (var kv in vertW[vi])
                        acc[kv.Key] = acc.TryGetValue(kv.Key, out float ex) ? ex + kv.Value : kv.Value;
                }
                float inv = 1f / mem.Count;
                var keys = new List<int>(acc.Keys);
                foreach (int b in keys) acc[b] *= inv;
                Normalize(acc);
                weldWeights[w] = acc;
                weldRestPos[w] = c * inv;
            }

            // welded adjacency from triangles
            neighbors = new HashSet<int>[weldCount];
            for (int w = 0; w < weldCount; w++) neighbors[w] = new HashSet<int>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                int[] tris = src.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = weldOf[tris[t]], b = weldOf[tris[t + 1]], c = weldOf[tris[t + 2]];
                    if (a != b) { neighbors[a].Add(b); neighbors[b].Add(a); }
                    if (b != c) { neighbors[b].Add(c); neighbors[c].Add(b); }
                    if (a != c) { neighbors[a].Add(c); neighbors[c].Add(a); }
                }
            }
        }

        /// <summary>Refresh baked world positions (throttled). Call before a brush stroke.</summary>
        public void RefreshWorldPositions(bool force = false)
        {
            if (smr == null) return;
            if (!force && Time.unscaledTime - _lastBakeTime < 0.1f && weldWorldPos != null) return;
            _lastBakeTime = Time.unscaledTime;

            if (_bakeTmp == null) _bakeTmp = new Mesh();
            LiveShapes.Bake(smr, _bakeTmp);   // shape keys as rendered, not VNyan's mid-frame zeros
            Vector3[] baked = _bakeTmp.vertices;
            Matrix4x4 l2w = smr.transform.localToWorldMatrix;

            if (weldWorldPos == null || weldWorldPos.Length != weldCount)
                weldWorldPos = new Vector3[weldCount];
            var sum = new Vector3[weldCount];
            var cnt = new int[weldCount];
            int n = Mathf.Min(baked.Length, vertexCount);
            for (int v = 0; v < n; v++)
            {
                int w = weldOf[v];
                sum[w] += baked[v];
                cnt[w]++;
            }
            for (int w = 0; w < weldCount; w++)
            {
                if (cnt[w] > 0)
                    weldWorldPos[w] = l2w.MultiplyPoint3x4(sum[w] / cnt[w]);
            }
        }

        /// <summary>Scatter weldWeights to every original vertex and write to the mesh.</summary>
        public void ApplyToMesh(int maxBonesPerVertex = 0, float minWeight = 0f)
        {
            if (smr == null || workingMesh == null) return;

            int maxObserved = 0;
            var outBpv = new NativeArray<byte>(vertexCount, Allocator.Temp);
            var flatOut = new List<BoneWeight1>(vertexCount * 4);
            var tmp = new List<KeyValuePair<int, float>>();

            for (int v = 0; v < vertexCount; v++)
            {
                int w = weldOf[v];
                var dict = weldWeights[w];

                tmp.Clear();
                foreach (var kv in dict)
                    if (kv.Value > minWeight) tmp.Add(kv);
                // sort descending: SetBoneWeights requires per-vertex descending order
                tmp.Sort((x, y) => y.Value.CompareTo(x.Value));

                if (maxBonesPerVertex > 0 && tmp.Count > maxBonesPerVertex)
                    tmp.RemoveRange(maxBonesPerVertex, tmp.Count - maxBonesPerVertex);

                if (tmp.Count == 0)
                {
                    outBpv[v] = 1;
                    flatOut.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
                    continue;
                }

                // renormalize this vertex
                float sum = 0f;
                for (int k = 0; k < tmp.Count; k++) sum += tmp[k].Value;
                float inv = sum > 0f ? 1f / sum : 0f;

                outBpv[v] = (byte)Mathf.Min(tmp.Count, 255);
                for (int k = 0; k < tmp.Count; k++)
                    flatOut.Add(new BoneWeight1 { boneIndex = tmp[k].Key, weight = tmp[k].Value * inv });

                if (tmp.Count > maxObserved) maxObserved = tmp.Count;
            }

            var outW = new NativeArray<BoneWeight1>(flatOut.Count, Allocator.Temp);
            for (int i = 0; i < flatOut.Count; i++) outW[i] = flatOut[i];
            workingMesh.SetBoneWeights(outBpv, outW);
            outBpv.Dispose();
            outW.Dispose();

            if (maxObserved > 4)
            {
                if (QualitySettings.skinWeights != SkinWeights.Unlimited)
                    QualitySettings.skinWeights = SkinWeights.Unlimited;
                smr.quality = SkinQuality.Auto;
            }
            dirty = false;
        }

        public int MaxBonesPerVertex()
        {
            int max = 0;
            for (int w = 0; w < weldCount; w++)
                if (weldWeights[w].Count > max) max = weldWeights[w].Count;
            return max;
        }

        // ---- undo / redo ----

        Dictionary<int, float>[] Snapshot()
        {
            var snap = new Dictionary<int, float>[weldCount];
            for (int w = 0; w < weldCount; w++)
                snap[w] = new Dictionary<int, float>(weldWeights[w]);
            return snap;
        }

        void Restore(Dictionary<int, float>[] snap)
        {
            int n = Math.Min(weldCount, snap.Length);
            for (int w = 0; w < n; w++)
                weldWeights[w] = new Dictionary<int, float>(snap[w]);
            dirty = true;
        }

        /// <summary>
        /// Capture the current weights so the very next edit can be undone.
        /// Call once at the START of a mutating action (e.g. a brush stroke, Smooth,
        /// Limit, Transfer). Clears the redo stack.
        /// </summary>
        public void PushUndo()
        {
            if (weldWeights == null) return;
            _undo.Add(Snapshot());
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>Revert to the state before the last PushUndo. Returns false if empty.</summary>
        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            _redo.Add(Snapshot());
            if (_redo.Count > MaxUndo) _redo.RemoveAt(0);
            var prev = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Restore(prev);
            return true;
        }

        /// <summary>Re-apply the last undone state. Returns false if empty.</summary>
        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            _undo.Add(Snapshot());
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            var next = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            Restore(next);
            return true;
        }

        public void ClearHistory() { _undo.Clear(); _redo.Clear(); }

        // ---- shared helpers ----

        public static void Normalize(Dictionary<int, float> w)
        {
            float sum = 0f;
            foreach (var kv in w) sum += kv.Value;
            if (sum <= 0f) return;
            float inv = 1f / sum;
            var keys = new List<int>(w.Keys);
            foreach (int b in keys) w[b] *= inv;
        }

        /// <summary>
        /// Set the active bone's weight on <paramref name="dict"/> to a target and
        /// rescale the remaining influences to fill the rest (Maya/Blender-style
        /// auto-normalize), keeping the total at 1.
        /// </summary>
        public static void SetWeightNormalized(Dictionary<int, float> dict, int activeBone, float target)
        {
            target = Mathf.Clamp01(target);
            float others = 0f;
            foreach (var kv in dict)
                if (kv.Key != activeBone) others += kv.Value;

            if (others <= 1e-6f)
            {
                // nothing else to balance against
                dict.Clear();
                dict[activeBone] = 1f;
                return;
            }

            float remain = 1f - target;
            float scale = remain / others;
            var keys = new List<int>(dict.Keys);
            foreach (int b in keys)
                if (b != activeBone) dict[b] = dict[b] * scale;
            if (target > 0f) dict[activeBone] = target;
            else dict.Remove(activeBone);

            Prune(dict, 1e-5f);
        }

        public static void Prune(Dictionary<int, float> w, float minWeight)
        {
            List<int> small = null;
            foreach (var kv in w)
                if (kv.Value < minWeight) (small ?? (small = new List<int>())).Add(kv.Key);
            if (small != null) foreach (int b in small) w.Remove(b);
        }
    }
}
