using System;
using System.Collections.Generic;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Generates colliders that hug the model closely. For every skinning bone it
    /// collects the vertices that bone dominates, lifts them into that bone's local
    /// (bind-pose) space, then fits either a <b>capsule</b> (when the point cloud is
    /// elongated) or a <b>sphere</b> using a PCA principal axis and a percentile
    /// perpendicular radius. Output is a list of pose-independent
    /// <see cref="GenCollider"/> records that the exporters translate into VRM
    /// spring-bone, DynamicBone, VRChat PhysBone, JayoPhysBones and JayoJiggle
    /// collider definitions, and into the VBX binary.
    ///
    /// Bone-local fitting uses the mesh bindposes, so the result does not depend on
    /// the avatar's current pose: <c>boneLocal = bindpose[boneIndex] * meshVertex</c>.
    /// </summary>
    public static class ColliderGen
    {
        public enum GenShape { Sphere, Capsule }

        /// <summary>A fitted collider, expressed in its bone's local space.</summary>
        public sealed class GenCollider
        {
            public string name;              // unique collider id (e.g. "Hips_col")
            public string boneName;          // transform the collider rides on
            public GenShape shape;
            public Vector3 center;           // bone-local sphere centre / capsule end A
            public Vector3 centerEnd;        // bone-local capsule end B (== center for sphere)
            public float radius;             // metres
            public int vertexCount;          // how many verts fed the fit (diagnostic)

            public float Height                       // capsule total height (centre-to-centre + 2r)
            {
                get { return (centerEnd - center).magnitude + 2f * radius; }
            }
            public Vector3 Axis                       // bone-local capsule axis (unit), up if degenerate
            {
                get { Vector3 d = centerEnd - center; return d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.up; }
            }
            public Vector3 Mid { get { return (center + centerEnd) * 0.5f; } }
        }

        [Serializable]
        public struct GenConfig
        {
            public float radiusScale;     // multiply fitted radius (1 = snug, >1 = looser)
            public float minWeight;       // a vertex joins a bone only if its top weight >= this
            public int minVerts;          // skip bones with fewer assigned verts than this
            public bool capsules;         // allow capsules (false = spheres only)
            public float capsuleAspect;   // length/diameter above which a capsule is used
            public float radiusPercentile;// 0..1 perpendicular distance percentile for radius (trims spikes)
            public bool humanoidOnly;     // only emit on mapped humanoid bones (needs animator)
            public bool skipFingers;      // drop the small finger/toe bones (noisy, rarely needed)

            public static GenConfig Default()
            {
                return new GenConfig
                {
                    radiusScale = 1.0f,
                    minWeight = 0.5f,
                    minVerts = 12,
                    capsules = true,
                    capsuleAspect = 1.6f,
                    radiusPercentile = 0.92f,
                    humanoidOnly = false,
                    skipFingers = true
                };
            }
        }

        public struct GenResult
        {
            public List<GenCollider> colliders;
            public int bonesConsidered;
            public string message;
        }

        // per-bone accumulation while scanning meshes
        sealed class BoneAccum
        {
            public Transform bone;
            public List<Vector3> pts = new List<Vector3>(64);
        }

        /// <summary>
        /// Fit colliders to <paramref name="renderers"/>. <paramref name="animator"/> may be
        /// null (only needed for humanoidOnly / finger filtering by humanoid map).
        /// </summary>
        public static GenResult Generate(IList<SkinnedMeshRenderer> renderers, Animator animator, GenConfig cfg)
        {
            var res = new GenResult { colliders = new List<GenCollider>(), bonesConsidered = 0, message = "" };
            if (renderers == null || renderers.Count == 0) { res.message = "No meshes."; return res; }

            // optional humanoid filtering sets
            HashSet<Transform> humanoidSet = null;
            HashSet<Transform> fingerSet = null;
            if ((cfg.humanoidOnly || cfg.skipFingers) && animator != null && animator.isHuman)
            {
                humanoidSet = new HashSet<Transform>();
                fingerSet = new HashSet<Transform>();
                for (int b = 0; b < (int)HumanBodyBones.LastBone; b++)
                {
                    Transform t = animator.GetBoneTransform((HumanBodyBones)b);
                    if (t == null) continue;
                    humanoidSet.Add(t);
                    string nm = ((HumanBodyBones)b).ToString();
                    if (nm.Contains("Thumb") || nm.Contains("Index") || nm.Contains("Middle") ||
                        nm.Contains("Ring") || nm.Contains("Little") || nm.Contains("Toes"))
                        fingerSet.Add(t);
                }
            }

            var accum = new Dictionary<Transform, BoneAccum>();

            foreach (var smr in renderers)
            {
                if (smr == null || smr.sharedMesh == null || !smr.sharedMesh.isReadable) continue;
                Mesh mesh = smr.sharedMesh;
                Transform[] bones = smr.bones;
                Matrix4x4[] bind = mesh.bindposes;
                if (bones == null || bones.Length == 0 || bind == null || bind.Length < bones.Length) continue;

                Vector3[] verts = mesh.vertices;
                var bpv = mesh.GetBonesPerVertex();
                var flat = mesh.GetAllBoneWeights();
                int off = 0;
                for (int v = 0; v < verts.Length; v++)
                {
                    int n = v < bpv.Length ? bpv[v] : 0;
                    // dominant influence for this vertex
                    int topBone = -1; float topW = 0f;
                    for (int k = 0; k < n; k++)
                    {
                        var bw = flat[off + k];
                        if (bw.weight > topW) { topW = bw.weight; topBone = bw.boneIndex; }
                    }
                    off += n;
                    if (topBone < 0 || topBone >= bones.Length) continue;
                    if (topW < cfg.minWeight) continue;
                    Transform bt = bones[topBone];
                    if (bt == null) continue;
                    if (humanoidSet != null && cfg.humanoidOnly && !humanoidSet.Contains(bt)) continue;
                    if (fingerSet != null && cfg.skipFingers && fingerSet.Contains(bt)) continue;

                    Vector3 local = bind[topBone].MultiplyPoint3x4(verts[v]); // mesh-local -> bone-local at bind
                    BoneAccum a;
                    if (!accum.TryGetValue(bt, out a)) { a = new BoneAccum { bone = bt }; accum[bt] = a; }
                    a.pts.Add(local);
                }
            }

            res.bonesConsidered = accum.Count;
            var usedNames = new HashSet<string>();
            foreach (var kv in accum)
            {
                BoneAccum a = kv.Value;
                if (a.pts.Count < cfg.minVerts) continue;
                GenCollider gc = Fit(a.pts, cfg);
                if (gc == null) continue;
                gc.boneName = a.bone.name;
                gc.name = UniqueName(a.bone.name, usedNames);
                res.colliders.Add(gc);
            }

            res.message = res.colliders.Count + " colliders on " + res.bonesConsidered + " bones";
            return res;
        }

        static string UniqueName(string baseName, HashSet<string> used)
        {
            string n = baseName + "_col";
            string c = n; int i = 1;
            while (used.Contains(c)) c = n + "_" + (i++);
            used.Add(c);
            return c;
        }

        // ---- fitting ----

        static GenCollider Fit(List<Vector3> pts, GenConfig cfg)
        {
            int n = pts.Count;
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < n; i++) mean += pts[i];
            mean /= n;

            // covariance (symmetric 3x3)
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = pts[i] - mean;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            Vector3 axis = PrincipalAxis(xx, xy, xz, yy, yz, zz);

            // project onto axis: extents + perpendicular distances
            float tMin = float.MaxValue, tMax = float.MinValue;
            var perp = new List<float>(n);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = pts[i] - mean;
                float t = Vector3.Dot(d, axis);
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
                Vector3 perpV = d - axis * t;
                perp.Add(perpV.magnitude);
            }
            perp.Sort();
            float radius = Percentile(perp, cfg.radiusPercentile);
            if (radius < 1e-4f) radius = 1e-4f;
            radius *= Mathf.Max(0.05f, cfg.radiusScale);

            float length = tMax - tMin;
            var gc = new GenCollider { vertexCount = n, radius = radius };

            if (cfg.capsules && length > radius * 2f * Mathf.Max(1.05f, cfg.capsuleAspect))
            {
                // capsule: place sphere centres inset by radius so the caps reach the extents
                float half = Mathf.Max(0f, length * 0.5f - radius);
                Vector3 mid = mean + axis * ((tMin + tMax) * 0.5f);
                gc.shape = GenShape.Capsule;
                gc.center = mid - axis * half;
                gc.centerEnd = mid + axis * half;
            }
            else
            {
                // sphere: cover the cloud (radius = max of perpendicular + half-length spread)
                gc.shape = GenShape.Sphere;
                float r = Mathf.Max(radius, length * 0.5f);
                gc.radius = r;
                gc.center = mean + axis * ((tMin + tMax) * 0.5f);
                gc.centerEnd = gc.center;
            }
            return gc;
        }

        // power iteration for the dominant eigenvector of a symmetric 3x3 matrix
        static Vector3 PrincipalAxis(float xx, float xy, float xz, float yy, float yz, float zz)
        {
            Vector3 v = new Vector3(1f, 1f, 1f).normalized;
            for (int it = 0; it < 24; it++)
            {
                Vector3 nv = new Vector3(
                    xx * v.x + xy * v.y + xz * v.z,
                    xy * v.x + yy * v.y + yz * v.z,
                    xz * v.x + yz * v.y + zz * v.z);
                float m = nv.magnitude;
                if (m < 1e-12f) return Vector3.up;
                nv /= m;
                if (Vector3.Dot(nv, v) > 0.9999999f) { v = nv; break; }
                v = nv;
            }
            return v.sqrMagnitude > 1e-10f ? v.normalized : Vector3.up;
        }

        static float Percentile(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return 0f;
            p = Mathf.Clamp01(p);
            float idx = p * (sorted.Count - 1);
            int lo = Mathf.FloorToInt(idx);
            int hi = Mathf.Min(lo + 1, sorted.Count - 1);
            float frac = idx - lo;
            return Mathf.Lerp(sorted[lo], sorted[hi], frac);
        }
    }
}
