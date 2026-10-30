using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Reads collider definitions back from any of the sidecar formats that
    /// <see cref="CollidersExport"/> writes:
    ///   • JayoPhysBones / JayoJiggle: colliders[] with bone/offset/radius(+offsetEnd/axis/height)
    ///   • VRChat PhysBone: colliders[] with rootTransform/shapeType/position/axis/height
    ///   • DynamicBone: colliders[] with bone/m_Center/m_Radius/m_Height/m_Direction
    /// The format is detected per entry from its field names, so mixed or hand-edited
    /// files still load. Output is bone-local <see cref="ColliderGen.GenCollider"/>s,
    /// the same records the editor and every exporter operate on.
    /// </summary>
    public static class ColliderImport
    {
        public static List<ColliderGen.GenCollider> Load(string path, out string formatSeen)
        {
            var result = new List<ColliderGen.GenCollider>();
            var formats = new HashSet<string>();

            JObject root = JObject.Parse(File.ReadAllText(path));
            JToken cols = root["colliders"];
            if (cols == null || cols.Type != JTokenType.Array)
            {
                formatSeen = "no colliders[] array";
                return result;
            }

            foreach (JToken e in cols)
            {
                if (e.Type != JTokenType.Object) continue;
                JObject o = (JObject)e;

                ColliderGen.GenCollider c = null;
                if (o["rootTransform"] != null) { c = FromVrc(o); formats.Add("VRChat PhysBones"); }
                else if (o["m_Center"] != null) { c = FromDynamicBone(o); formats.Add("DynamicBone"); }
                else if (o["offset"] != null) { c = FromJayo(o); formats.Add("PhysBones/Jiggle"); }
                if (c != null) result.Add(c);
            }

            formatSeen = formats.Count > 0 ? string.Join(" + ", new List<string>(formats).ToArray()) : "unknown";
            return result;
        }

        // ---- JayoPhysBones / JayoJiggle: offset is capsule end A when offsetEnd is
        // present, otherwise the capsule CENTRE with axis+height (total, incl. caps) ----
        static ColliderGen.GenCollider FromJayo(JObject o)
        {
            string type = Str(o, "type", "sphere").ToLowerInvariant();
            string bone = Str(o, "bone", null);
            if (string.IsNullOrEmpty(bone)) return null;

            var c = new ColliderGen.GenCollider
            {
                name = Str(o, "name", bone + "_col"),
                boneName = bone,
                radius = F(o, "radius", 0.05f)
            };

            Vector3 offset = V3(o["offset"], Vector3.zero);
            if (type == "capsule")
            {
                c.shape = ColliderGen.GenShape.Capsule;
                if (o["offsetEnd"] != null)
                {
                    c.center = offset;
                    c.centerEnd = V3(o["offsetEnd"], offset);
                }
                else
                {
                    Vector3 axis = V3(o["axis"], Vector3.up);
                    if (axis.sqrMagnitude < 1e-10f) axis = Vector3.up;
                    axis.Normalize();
                    float half = Mathf.Max(0f, F(o, "height", 0f) * 0.5f - c.radius);
                    c.center = offset - axis * half;
                    c.centerEnd = offset + axis * half;
                }
            }
            else
            {
                c.shape = ColliderGen.GenShape.Sphere;
                c.center = offset;
                c.centerEnd = offset;
            }
            return c;
        }

        // ---- VRChat PhysBone: position is the collider centre; height is total ----
        static ColliderGen.GenCollider FromVrc(JObject o)
        {
            string bone = Str(o, "rootTransform", null);
            if (string.IsNullOrEmpty(bone)) return null;

            var c = new ColliderGen.GenCollider
            {
                name = Str(o, "name", bone + "_col"),
                boneName = bone,
                radius = F(o, "radius", 0.05f)
            };

            Vector3 mid = V3(o["position"], Vector3.zero);
            bool capsule = Str(o, "shapeType", "Sphere").ToLowerInvariant() == "capsule";
            if (capsule)
            {
                Vector3 axis = V3(o["axis"], Vector3.up);
                if (axis.sqrMagnitude < 1e-10f) axis = Vector3.up;
                axis.Normalize();
                float half = Mathf.Max(0f, F(o, "height", 0f) * 0.5f - c.radius);
                c.shape = ColliderGen.GenShape.Capsule;
                c.center = mid - axis * half;
                c.centerEnd = mid + axis * half;
            }
            else
            {
                c.shape = ColliderGen.GenShape.Sphere;
                c.center = mid;
                c.centerEnd = mid;
            }
            return c;
        }

        // ---- DynamicBone: m_Center is the centre; m_Direction 0/1/2 = X/Y/Z axis ----
        static ColliderGen.GenCollider FromDynamicBone(JObject o)
        {
            string bone = Str(o, "bone", null);
            if (string.IsNullOrEmpty(bone)) return null;

            var c = new ColliderGen.GenCollider
            {
                name = Str(o, "name", bone + "_col"),
                boneName = bone,
                radius = F(o, "m_Radius", 0.05f)
            };

            Vector3 mid = V3(o["m_Center"], Vector3.zero);
            float h = F(o, "m_Height", 0f);
            if (h > c.radius * 2f + 1e-5f)
            {
                int dir = (int)F(o, "m_Direction", 1f);
                Vector3 axis = dir == 0 ? Vector3.right : dir == 2 ? Vector3.forward : Vector3.up;
                float half = Mathf.Max(0f, h * 0.5f - c.radius);
                c.shape = ColliderGen.GenShape.Capsule;
                c.center = mid - axis * half;
                c.centerEnd = mid + axis * half;
            }
            else
            {
                c.shape = ColliderGen.GenShape.Sphere;
                c.center = mid;
                c.centerEnd = mid;
            }
            return c;
        }

        // ---- tiny JToken helpers ----

        static string Str(JObject o, string key, string def)
        {
            JToken t = o[key];
            return t != null && t.Type == JTokenType.String ? (string)t : def;
        }

        static float F(JObject o, string key, float def)
        {
            JToken t = o[key];
            if (t == null) return def;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
            return def;
        }

        static Vector3 V3(JToken t, Vector3 def)
        {
            if (t == null || t.Type != JTokenType.Array) return def;
            JArray a = (JArray)t;
            if (a.Count < 3) return def;
            return new Vector3((float)a[0], (float)a[1], (float)a[2]);
        }
    }
}
