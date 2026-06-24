using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Writes the generated colliders out as sidecar config files for every supported
    /// physics system, so a single auto-fit pass can be consumed by:
    ///   • JayoPhysBones  → physbones.json   (the user's VNyan PhysBones plugin)
    ///   • JayoJiggle     → jigglephysics.json(the user's VNyan jiggle plugin)
    ///   • VRChat PhysBone→ *.vrcphysbones.json (VRCPhysBoneCollider field layout)
    ///   • DynamicBone    → *.dynamicbone.json  (DynamicBoneCollider field layout)
    /// VRM 0.x / VRM 1.0 spring-bone colliders are embedded directly inside the
    /// exported .vrm by <see cref="GltfVrmExporter"/>; the .vbx embeds them natively.
    ///
    /// All offsets are in Unity bone-local space (metres), matching the live skeleton,
    /// so the consuming plugin attaches them to the bone named in each entry.
    /// </summary>
    public static class CollidersExport
    {
        static float[] V3(Vector3 v) { return new float[] { v.x, v.y, v.z }; }

        /// <summary>Write every sidecar JSON for the given colliders next to baseNoExt.</summary>
        public static List<string> WriteAll(string baseNoExt, IList<ColliderGen.GenCollider> colliders)
        {
            var written = new List<string>();
            if (colliders == null || colliders.Count == 0) return written;

            written.Add(Write(baseNoExt + ".physbones.json", JayoPhysBones(colliders)));
            written.Add(Write(baseNoExt + ".jigglephysics.json", JayoJiggle(colliders)));
            written.Add(Write(baseNoExt + ".vrcphysbones.json", VrchatPhysBone(colliders)));
            written.Add(Write(baseNoExt + ".dynamicbone.json", DynamicBone(colliders)));
            return written;
        }

        static string Write(string path, object payload)
        {
            string json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            File.WriteAllText(path, json);
            return path;
        }

        // ---- JayoPhysBones (physbones.json) — matches PhysBoneConfig field layout ----
        public static object JayoPhysBones(IList<ColliderGen.GenCollider> cols)
        {
            var list = new List<object>();
            foreach (var c in cols)
            {
                var d = new Dictionary<string, object>
                {
                    { "name", c.name },
                    { "type", c.shape == ColliderGen.GenShape.Capsule ? "capsule" : "sphere" },
                    { "bone", c.boneName },
                    { "offset", V3(c.center) },
                    { "radius", c.radius }
                };
                if (c.shape == ColliderGen.GenShape.Capsule)
                {
                    d["offsetEnd"] = V3(c.centerEnd);
                    d["axis"] = V3(c.Axis);
                    d["height"] = c.Height;
                }
                list.Add(d);
            }
            return new Dictionary<string, object>
            {
                { "settings", new Dictionary<string, object> { { "enabled", true }, { "substeps", 2 } } },
                { "colliders", list },
                { "chains", new List<object>() }
            };
        }

        // ---- JayoJiggle (jigglephysics.json) — matches JiggleConfig field layout ----
        public static object JayoJiggle(IList<ColliderGen.GenCollider> cols)
        {
            var list = new List<object>();
            foreach (var c in cols)
            {
                var d = new Dictionary<string, object>
                {
                    { "name", c.name },
                    { "type", c.shape == ColliderGen.GenShape.Capsule ? "capsule" : "sphere" },
                    { "bone", c.boneName },
                    { "offset", V3(c.center) },
                    { "radius", c.radius }
                };
                if (c.shape == ColliderGen.GenShape.Capsule)
                {
                    d["offsetEnd"] = V3(c.centerEnd);
                    d["axis"] = V3(c.Axis);
                    d["height"] = c.Height;
                }
                list.Add(d);
            }
            return new Dictionary<string, object>
            {
                { "settings", new Dictionary<string, object> { { "enabled", true }, { "substeps", 2 } } },
                { "colliders", list },
                { "bones", new List<object>() }
            };
        }

        // ---- VRChat PhysBone collider descriptor ----
        // Mirrors VRCPhysBoneCollider: shapeType, radius, height, position, axis, rootTransform.
        public static object VrchatPhysBone(IList<ColliderGen.GenCollider> cols)
        {
            var list = new List<object>();
            foreach (var c in cols)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "name", c.name },
                    { "rootTransform", c.boneName },
                    { "shapeType", c.shape == ColliderGen.GenShape.Capsule ? "Capsule" : "Sphere" },
                    { "radius", c.radius },
                    { "height", c.shape == ColliderGen.GenShape.Capsule ? c.Height : 0f },
                    { "position", V3(c.Mid) },     // VRChat positions the collider at its centre
                    { "axis", V3(c.Axis) },        // capsule long axis (local)
                    { "insideBounds", false }
                });
            }
            return new Dictionary<string, object> { { "version", 1 }, { "colliders", list } };
        }

        // ---- DynamicBone collider descriptor ----
        // Mirrors DynamicBoneCollider: m_Center, m_Radius, m_Height, m_Direction, m_Bound.
        public static object DynamicBone(IList<ColliderGen.GenCollider> cols)
        {
            var list = new List<object>();
            foreach (var c in cols)
            {
                // DynamicBone capsules are axis-aligned X/Y/Z; pick the dominant axis.
                Vector3 a = c.Axis;
                int dir = 1; // 0=X,1=Y,2=Z
                float ax = Mathf.Abs(a.x), ay = Mathf.Abs(a.y), az = Mathf.Abs(a.z);
                if (ax >= ay && ax >= az) dir = 0;
                else if (az >= ax && az >= ay) dir = 2;
                list.Add(new Dictionary<string, object>
                {
                    { "name", c.name },
                    { "bone", c.boneName },
                    { "m_Center", V3(c.Mid) },
                    { "m_Radius", c.radius },
                    { "m_Height", c.shape == ColliderGen.GenShape.Capsule ? c.Height : 0f },
                    { "m_Direction", dir },     // 0=X 1=Y 2=Z
                    { "m_Bound", 0 }            // 0 = Outside
                });
            }
            return new Dictionary<string, object> { { "version", 1 }, { "colliders", list } };
        }
    }
}
