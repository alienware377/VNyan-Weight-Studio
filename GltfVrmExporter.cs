using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Self-contained glTF 2.0 (.glb) writer that emits VRM 0.x or VRM 1.0 avatars,
    /// with no dependency on the UniVRM runtime version. Exports geometry, skinning,
    /// blendshapes (morph targets), base-colour materials + textures, and the VRM
    /// humanoid bone map.
    ///
    /// LIMITATION: glTF/VRM skinning is 4 influences per vertex (JOINTS_0/WEIGHTS_0).
    /// Vertices with more influences are reduced to their 4 strongest and renormalized.
    /// For full &gt;4-influence fidelity, export .vbx instead.
    ///
    /// Unity (left-handed, +Z forward) is converted to glTF (right-handed) by negating
    /// Z on all positions/vectors, converting node rotations (q.x,q.y -> negated) and
    /// bind matrices (S·M·S), and reversing triangle winding.
    /// </summary>
    public static class GltfVrmExporter
    {
        public enum VrmVersion { Vrm0, Vrm1 }

        // S = diag(1,1,-1,1): Unity<->glTF basis change (its own inverse)
        static readonly Matrix4x4 S = Matrix4x4.Scale(new Vector3(1, 1, -1));

        public static void Export(string path, VrmVersion version, Transform avatarRoot,
            IList<SkinnedMeshRenderer> renderers, Animator animator, VrmMeta meta,
            IList<ColliderGen.GenCollider> colliders = null)
        {
            var b = new GltfBuilder();
            b.Build(version, avatarRoot, renderers, animator, meta ?? VrmMeta.Default(), colliders);
            byte[] glb = b.ToGlb();
            File.WriteAllBytes(path, glb);
        }

        public sealed class VrmMeta
        {
            public string title = "WeightStudio Export";
            public string author = "Unknown";
            public string version = "1.0";
            public static VrmMeta Default() { return new VrmMeta(); }
        }

        // ---- Unity -> glTF conversions ----
        static float[] CPos(Vector3 v) { return new float[] { v.x, v.y, -v.z }; }
        static float[] CQuat(Quaternion q) { return new float[] { -q.x, -q.y, q.z, q.w }; }
        static Matrix4x4 CMat(Matrix4x4 m) { return S * m * S; }

        sealed class GltfBuilder
        {
            readonly List<object> _bufferViews = new List<object>();
            readonly List<object> _accessors = new List<object>();
            readonly List<object> _meshes = new List<object>();
            readonly List<object> _materials = new List<object>();
            readonly List<object> _textures = new List<object>();
            readonly List<object> _images = new List<object>();
            readonly List<object> _samplers = new List<object>();
            readonly List<object> _nodes = new List<object>();
            readonly List<object> _skins = new List<object>();
            readonly MemoryStream _bin = new MemoryStream();

            Dictionary<string, object> _root;

            public void Build(VrmVersion version, Transform avatarRoot,
                IList<SkinnedMeshRenderer> renderers, Animator animator, VrmMeta meta,
                IList<ColliderGen.GenCollider> colliders = null)
            {
                // index avatar hierarchy -> node list
                var index = new Dictionary<Transform, int>();
                var order = new List<Transform>();
                IndexHierarchy(avatarRoot, index, order);

                // bone name -> node index (for attaching spring-bone colliders)
                var nameToNode = new Dictionary<string, int>();
                for (int i = 0; i < order.Count; i++)
                    if (!nameToNode.ContainsKey(order[i].name)) nameToNode[order[i].name] = i;

                // create skeleton nodes (children filled after)
                var childLists = new List<List<int>>();
                for (int i = 0; i < order.Count; i++)
                {
                    Transform t = order[i];
                    var node = new Dictionary<string, object>();
                    node["name"] = t.name;
                    node["translation"] = CPos(t.localPosition);
                    node["rotation"] = CQuat(t.localRotation);
                    node["scale"] = new float[] { t.localScale.x, t.localScale.y, t.localScale.z };
                    _nodes.Add(node);
                    childLists.Add(new List<int>());
                }
                for (int i = 0; i < order.Count; i++)
                {
                    Transform parent = order[i].parent;
                    if (parent != null && index.TryGetValue(parent, out int pi))
                        childLists[pi].Add(i);
                }

                var sceneNodes = new List<int>();
                // root nodes (no indexed parent)
                for (int i = 0; i < order.Count; i++)
                    if (order[i].parent == null || !index.ContainsKey(order[i].parent))
                        sceneNodes.Add(i);

                // meshes + skins + mesh nodes
                foreach (var smr in renderers)
                {
                    if (smr == null || smr.sharedMesh == null || !smr.sharedMesh.isReadable) continue;
                    int meshIdx = BuildMesh(smr);
                    if (meshIdx < 0) continue;
                    int skinIdx = BuildSkin(smr, index);

                    var node = new Dictionary<string, object>();
                    node["name"] = smr.name;
                    node["mesh"] = meshIdx;
                    if (skinIdx >= 0) node["skin"] = skinIdx;
                    int nodeIdx = _nodes.Count;
                    _nodes.Add(node);
                    childLists.Add(new List<int>());
                    sceneNodes.Add(nodeIdx);
                }

                // attach children arrays
                for (int i = 0; i < _nodes.Count && i < childLists.Count; i++)
                    if (childLists[i].Count > 0)
                        ((Dictionary<string, object>)_nodes[i])["children"] = childLists[i].ToArray();

                _root = new Dictionary<string, object>();
                _root["asset"] = new Dictionary<string, object> { { "version", "2.0" }, { "generator", "VNyan Weight Studio" } };
                _root["scene"] = 0;
                _root["scenes"] = new object[] { new Dictionary<string, object> { { "nodes", sceneNodes.ToArray() } } };
                _root["nodes"] = _nodes;
                if (_meshes.Count > 0) _root["meshes"] = _meshes;
                if (_skins.Count > 0) _root["skins"] = _skins;
                if (_materials.Count > 0) _root["materials"] = _materials;
                if (_textures.Count > 0) _root["textures"] = _textures;
                if (_images.Count > 0) _root["images"] = _images;
                if (_samplers.Count > 0) _root["samplers"] = _samplers;
                _root["bufferViews"] = _bufferViews;
                _root["accessors"] = _accessors;
                _root["buffers"] = new object[] { new Dictionary<string, object> { { "byteLength", _bin.Length } } };

                var used = new List<string>();
                var ext = new Dictionary<string, object>();
                if (version == VrmVersion.Vrm0)
                {
                    ext["VRM"] = BuildVrm0(index, animator, meta, colliders, nameToNode);
                    used.Add("VRM");
                }
                else
                {
                    ext["VRMC_vrm"] = BuildVrm1(index, animator, meta);
                    used.Add("VRMC_vrm");
                    object springBone = BuildVrm1SpringBone(colliders, nameToNode);
                    if (springBone != null) { ext["VRMC_springBone"] = springBone; used.Add("VRMC_springBone"); }
                }
                _root["extensions"] = ext;
                _root["extensionsUsed"] = used.ToArray();
            }

            // ---------- geometry ----------

            int BuildMesh(SkinnedMeshRenderer smr)
            {
                Mesh mesh = smr.sharedMesh;
                int vc = mesh.vertexCount;

                Vector3[] verts = mesh.vertices;
                Vector3[] norms = mesh.normals;
                Vector4[] tans = mesh.tangents;
                Vector2[] uv = mesh.uv;

                // positions (with min/max), normals, tangents, uv
                int posAcc = AddVec3(verts, true, true);
                int normAcc = (norms != null && norms.Length == vc) ? AddVec3(norms, true, false) : -1;
                int tanAcc = (tans != null && tans.Length == vc) ? AddTangents(tans) : -1;
                int uvAcc = (uv != null && uv.Length == vc) ? AddVec2(uv) : -1;

                // weights -> top 4
                BuildTop4(mesh, out ushort[] joints, out float[] weights);
                int jointsAcc = AddJoints(joints, vc);
                int weightsAcc = AddWeights(weights, vc);

                // blendshape morph targets
                var targets = new List<object>();
                var targetNames = new List<string>();
                for (int bi = 0; bi < mesh.blendShapeCount; bi++)
                {
                    int frames = mesh.GetBlendShapeFrameCount(bi);
                    if (frames == 0) continue;
                    var dV = new Vector3[vc]; var dN = new Vector3[vc]; var dT = new Vector3[vc];
                    mesh.GetBlendShapeFrameVertices(bi, frames - 1, dV, dN, dT); // last (full) frame
                    var tgt = new Dictionary<string, object>();
                    tgt["POSITION"] = AddVec3(dV, true, true);
                    if (norms != null && norms.Length == vc) tgt["NORMAL"] = AddVec3(dN, true, false);
                    targets.Add(tgt);
                    targetNames.Add(mesh.GetBlendShapeName(bi));
                }

                // primitives per submesh
                var prims = new List<object>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    int[] tris = mesh.GetTriangles(s);
                    int idxAcc = AddIndicesReversed(tris);

                    var attr = new Dictionary<string, object>();
                    attr["POSITION"] = posAcc;
                    if (normAcc >= 0) attr["NORMAL"] = normAcc;
                    if (tanAcc >= 0) attr["TANGENT"] = tanAcc;
                    if (uvAcc >= 0) attr["TEXCOORD_0"] = uvAcc;
                    attr["JOINTS_0"] = jointsAcc;
                    attr["WEIGHTS_0"] = weightsAcc;

                    var prim = new Dictionary<string, object>();
                    prim["attributes"] = attr;
                    prim["indices"] = idxAcc;
                    prim["mode"] = 4;
                    int matIdx = BuildMaterial(s < smr.sharedMaterials.Length ? smr.sharedMaterials[s] : null);
                    if (matIdx >= 0) prim["material"] = matIdx;
                    if (targets.Count > 0) prim["targets"] = targets;
                    prims.Add(prim);
                }

                var m = new Dictionary<string, object>();
                m["name"] = smr.name;
                m["primitives"] = prims;
                if (targetNames.Count > 0)
                    m["extras"] = new Dictionary<string, object> { { "targetNames", targetNames.ToArray() } };
                int mi = _meshes.Count;
                _meshes.Add(m);
                return mi;
            }

            int BuildSkin(SkinnedMeshRenderer smr, Dictionary<Transform, int> index)
            {
                Transform[] bones = smr.bones;
                if (bones == null || bones.Length == 0) return -1;
                Matrix4x4[] bind = smr.sharedMesh.bindposes;

                var joints = new int[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                    joints[i] = (bones[i] != null && index.TryGetValue(bones[i], out int bi)) ? bi : 0;

                // inverse bind matrices, converted
                var ibm = new float[bind.Length * 16];
                for (int i = 0; i < bind.Length; i++)
                {
                    Matrix4x4 c = CMat(bind[i]);
                    for (int k = 0; k < 16; k++) ibm[i * 16 + k] = c[k]; // column-major (Unity m[k] is column-major)
                }
                int ibmAcc = AddMatrices(ibm, bind.Length);

                var skin = new Dictionary<string, object>();
                skin["joints"] = joints;
                if (bind.Length == bones.Length) skin["inverseBindMatrices"] = ibmAcc;
                if (smr.rootBone != null && index.TryGetValue(smr.rootBone, out int rb)) skin["skeleton"] = rb;
                int si = _skins.Count;
                _skins.Add(skin);
                return si;
            }

            void BuildTop4(Mesh mesh, out ushort[] joints, out float[] weights)
            {
                int vc = mesh.vertexCount;
                joints = new ushort[vc * 4];
                weights = new float[vc * 4];
                var bpv = mesh.GetBonesPerVertex();
                var flat = mesh.GetAllBoneWeights();
                int off = 0;
                var buf = new List<KeyValuePair<int, float>>(8);
                for (int v = 0; v < vc; v++)
                {
                    int n = v < bpv.Length ? bpv[v] : 0;
                    buf.Clear();
                    for (int k = 0; k < n; k++)
                    {
                        var bw = flat[off + k];
                        buf.Add(new KeyValuePair<int, float>(bw.boneIndex, bw.weight));
                    }
                    off += n;
                    buf.Sort((x, y) => y.Value.CompareTo(x.Value));
                    int take = Mathf.Min(4, buf.Count);
                    float sum = 0f;
                    for (int k = 0; k < take; k++) sum += buf[k].Value;
                    float inv = sum > 0f ? 1f / sum : 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        if (k < take)
                        {
                            joints[v * 4 + k] = (ushort)buf[k].Key;
                            weights[v * 4 + k] = buf[k].Value * inv;
                        }
                        else { joints[v * 4 + k] = 0; weights[v * 4 + k] = 0f; }
                    }
                }
            }

            // ---------- materials ----------

            int BuildMaterial(Material mat)
            {
                var m = new Dictionary<string, object>();
                m["name"] = mat != null ? mat.name : "material";
                m["doubleSided"] = true;

                Color baseColor = Color.white;
                if (mat != null)
                {
                    if (mat.HasProperty("_Color")) baseColor = mat.GetColor("_Color");
                    else if (mat.HasProperty("_BaseColor")) baseColor = mat.GetColor("_BaseColor");
                }

                var pbr = new Dictionary<string, object>();
                pbr["baseColorFactor"] = new float[] { baseColor.r, baseColor.g, baseColor.b, baseColor.a };
                pbr["metallicFactor"] = 0f;
                pbr["roughnessFactor"] = 1f;

                byte[] png = mat != null ? VbxExporter.TextureToPng(GetTex(mat, "_MainTex", "_BaseMap")) : null;
                if (png != null)
                {
                    int tex = AddTexture(png);
                    pbr["baseColorTexture"] = new Dictionary<string, object> { { "index", tex } };
                }
                m["pbrMetallicRoughness"] = pbr;

                // alpha mode heuristic
                if (mat != null && mat.HasProperty("_Cutoff") && IsCutout(mat))
                {
                    m["alphaMode"] = "MASK";
                    m["alphaCutoff"] = mat.GetFloat("_Cutoff");
                }
                else if (baseColor.a < 0.999f || (mat != null && IsTransparent(mat)))
                {
                    m["alphaMode"] = "BLEND";
                }

                int mi = _materials.Count;
                _materials.Add(m);
                return mi;
            }

            static bool IsCutout(Material m)
            {
                return m.renderQueue >= 2225 && m.renderQueue < 2900 || HasKeyword(m, "_ALPHATEST_ON");
            }
            static bool IsTransparent(Material m)
            {
                return m.renderQueue >= 2900 || HasKeyword(m, "_ALPHABLEND_ON") || HasKeyword(m, "_SURFACE_TYPE_TRANSPARENT");
            }
            static bool HasKeyword(Material m, string k)
            {
                try { return m.IsKeywordEnabled(k); } catch { return false; }
            }

            static Texture GetTex(Material mat, string a, string b)
            {
                if (mat.HasProperty(a)) { var t = mat.GetTexture(a); if (t != null) return t; }
                if (b != null && mat.HasProperty(b)) { var t = mat.GetTexture(b); if (t != null) return t; }
                return null;
            }

            int AddTexture(byte[] png)
            {
                int viewIdx = AddBufferView(png, 0);
                var img = new Dictionary<string, object> { { "mimeType", "image/png" }, { "bufferView", viewIdx } };
                int imgIdx = _images.Count; _images.Add(img);
                if (_samplers.Count == 0)
                    _samplers.Add(new Dictionary<string, object> { { "magFilter", 9729 }, { "minFilter", 9987 }, { "wrapS", 10497 }, { "wrapT", 10497 } });
                var tex = new Dictionary<string, object> { { "source", imgIdx }, { "sampler", 0 } };
                int ti = _textures.Count; _textures.Add(tex);
                return ti;
            }

            // ---------- VRM extension blocks ----------

            object BuildVrm0(Dictionary<Transform, int> index, Animator animator, VrmMeta meta,
                IList<ColliderGen.GenCollider> colliders, Dictionary<string, int> nameToNode)
            {
                var humanBones = new List<object>();
                if (animator != null && animator.isHuman)
                {
                    foreach (var kv in Vrm0BoneNames)
                    {
                        Transform t = animator.GetBoneTransform(kv.Key);
                        if (t != null && index.TryGetValue(t, out int ni))
                            humanBones.Add(new Dictionary<string, object> { { "bone", kv.Value }, { "node", ni } });
                    }
                }
                return new Dictionary<string, object>
                {
                    { "exporterVersion", "VNyanWeightStudio-1.0" },
                    { "specVersion", "0.0" },
                    { "meta", new Dictionary<string, object> {
                        { "title", meta.title }, { "author", meta.author }, { "version", meta.version },
                        { "allowedUserName", "OnlyAuthor" }, { "violentUssageName", "Disallow" },
                        { "sexualUssageName", "Disallow" }, { "commercialUssageName", "Disallow" },
                        { "licenseName", "Other" } } },
                    { "humanoid", new Dictionary<string, object> { { "humanBones", humanBones } } },
                    { "firstPerson", new Dictionary<string, object>() },
                    { "blendShapeMaster", new Dictionary<string, object> { { "blendShapeGroups", new object[0] } } },
                    { "secondaryAnimation", new Dictionary<string, object> {
                        { "boneGroups", new object[0] },
                        { "colliderGroups", BuildVrm0ColliderGroups(colliders, nameToNode) } } },
                    { "materialProperties", new object[0] }
                };
            }

            // VRM 0.x supports spheres only; capsules are approximated by 3 spheres along the segment.
            object[] BuildVrm0ColliderGroups(IList<ColliderGen.GenCollider> colliders, Dictionary<string, int> nameToNode)
            {
                if (colliders == null || colliders.Count == 0) return new object[0];
                // group colliders by node
                var byNode = new Dictionary<int, List<object>>();
                foreach (var c in colliders)
                {
                    int node;
                    if (c == null || string.IsNullOrEmpty(c.boneName) || !nameToNode.TryGetValue(c.boneName, out node)) continue;
                    List<object> sph;
                    if (!byNode.TryGetValue(node, out sph)) { sph = new List<object>(); byNode[node] = sph; }
                    if (c.shape == ColliderGen.GenShape.Capsule)
                    {
                        sph.Add(Vrm0Sphere(c.center, c.radius));
                        sph.Add(Vrm0Sphere(c.Mid, c.radius));
                        sph.Add(Vrm0Sphere(c.centerEnd, c.radius));
                    }
                    else sph.Add(Vrm0Sphere(c.center, c.radius));
                }
                var groups = new List<object>();
                foreach (var kv in byNode)
                    groups.Add(new Dictionary<string, object> { { "node", kv.Key }, { "colliders", kv.Value.ToArray() } });
                return groups.ToArray();
            }

            static object Vrm0Sphere(Vector3 localCenter, float radius)
            {
                float[] o = CPos(localCenter); // Unity bone-local -> glTF
                return new Dictionary<string, object>
                {
                    { "offset", new Dictionary<string, object> { { "x", o[0] }, { "y", o[1] }, { "z", o[2] } } },
                    { "radius", radius }
                };
            }

            // VRM 1.0 VRMC_springBone: native sphere + capsule colliders, one group per collider.
            object BuildVrm1SpringBone(IList<ColliderGen.GenCollider> colliders, Dictionary<string, int> nameToNode)
            {
                if (colliders == null || colliders.Count == 0) return null;
                var colList = new List<object>();
                var groups = new List<object>();
                foreach (var c in colliders)
                {
                    int node;
                    if (c == null || string.IsNullOrEmpty(c.boneName) || !nameToNode.TryGetValue(c.boneName, out node)) continue;
                    object shape;
                    if (c.shape == ColliderGen.GenShape.Capsule)
                        shape = new Dictionary<string, object> { { "capsule", new Dictionary<string, object> {
                            { "offset", CPos(c.center) }, { "radius", c.radius }, { "tail", CPos(c.centerEnd) } } } };
                    else
                        shape = new Dictionary<string, object> { { "sphere", new Dictionary<string, object> {
                            { "offset", CPos(c.center) }, { "radius", c.radius } } } };
                    int idx = colList.Count;
                    colList.Add(new Dictionary<string, object> { { "node", node }, { "shape", shape } });
                    groups.Add(new Dictionary<string, object> { { "name", c.name }, { "colliders", new object[] { idx } } });
                }
                if (colList.Count == 0) return null;
                return new Dictionary<string, object>
                {
                    { "specVersion", "1.0" },
                    { "colliders", colList },
                    { "colliderGroups", groups },
                    { "springs", new object[0] }
                };
            }

            object BuildVrm1(Dictionary<Transform, int> index, Animator animator, VrmMeta meta)
            {
                var humanBones = new Dictionary<string, object>();
                if (animator != null && animator.isHuman)
                {
                    foreach (var kv in Vrm1BoneNames)
                    {
                        Transform t = animator.GetBoneTransform(kv.Key);
                        if (t != null && index.TryGetValue(t, out int ni))
                            humanBones[kv.Value] = new Dictionary<string, object> { { "node", ni } };
                    }
                }
                return new Dictionary<string, object>
                {
                    { "specVersion", "1.0" },
                    { "meta", new Dictionary<string, object> {
                        { "name", meta.title }, { "authors", new object[] { meta.author } },
                        { "version", meta.version },
                        { "licenseUrl", "https://vrm.dev/licenses/1.0/" },
                        { "avatarPermission", "onlyAuthor" }, { "commercialUsage", "personalNonProfit" },
                        { "creditNotation", "required" }, { "allowRedistribution", false },
                        { "modification", "prohibited" } } },
                    { "humanoid", new Dictionary<string, object> { { "humanBones", humanBones } } }
                };
            }

            // ---------- accessor / bufferView helpers ----------

            int AddBufferView(byte[] data, int target)
            {
                Align(4);
                long offset = _bin.Position;
                _bin.Write(data, 0, data.Length);
                var bv = new Dictionary<string, object> { { "buffer", 0 }, { "byteOffset", offset }, { "byteLength", data.Length } };
                if (target != 0) bv["target"] = target;
                int idx = _bufferViews.Count; _bufferViews.Add(bv);
                return idx;
            }

            void Align(int n)
            {
                long pad = (n - (_bin.Position % n)) % n;
                for (int i = 0; i < pad; i++) _bin.WriteByte(0);
            }

            int AddAccessor(int bufferView, int componentType, int count, string type, float[] min, float[] max, int byteOffset = 0)
            {
                var a = new Dictionary<string, object>
                {
                    { "bufferView", bufferView }, { "byteOffset", byteOffset },
                    { "componentType", componentType }, { "count", count }, { "type", type }
                };
                if (min != null) a["min"] = min;
                if (max != null) a["max"] = max;
                int idx = _accessors.Count; _accessors.Add(a);
                return idx;
            }

            int AddVec3(Vector3[] data, bool convertZ, bool withMinMax)
            {
                int n = data.Length;
                var bytes = new byte[n * 12];
                var mn = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var mx = new float[] { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < n; i++)
                {
                    float x = data[i].x, y = data[i].y, z = convertZ ? -data[i].z : data[i].z;
                    PutF(bytes, i * 12 + 0, x); PutF(bytes, i * 12 + 4, y); PutF(bytes, i * 12 + 8, z);
                    if (x < mn[0]) mn[0] = x; if (y < mn[1]) mn[1] = y; if (z < mn[2]) mn[2] = z;
                    if (x > mx[0]) mx[0] = x; if (y > mx[1]) mx[1] = y; if (z > mx[2]) mx[2] = z;
                }
                int bv = AddBufferView(bytes, 34962);
                return AddAccessor(bv, 5126, n, "VEC3", withMinMax ? mn : null, withMinMax ? mx : null);
            }

            int AddTangents(Vector4[] data)
            {
                int n = data.Length;
                var bytes = new byte[n * 16];
                for (int i = 0; i < n; i++)
                {
                    PutF(bytes, i * 16 + 0, data[i].x);
                    PutF(bytes, i * 16 + 4, data[i].y);
                    PutF(bytes, i * 16 + 8, -data[i].z);
                    PutF(bytes, i * 16 + 12, -data[i].w); // handedness flips under reflection
                }
                int bv = AddBufferView(bytes, 34962);
                return AddAccessor(bv, 5126, n, "VEC4", null, null);
            }

            int AddVec2(Vector2[] data)
            {
                int n = data.Length;
                var bytes = new byte[n * 8];
                for (int i = 0; i < n; i++)
                {
                    PutF(bytes, i * 8 + 0, data[i].x);
                    PutF(bytes, i * 8 + 4, 1f - data[i].y); // glTF UV origin top-left
                }
                int bv = AddBufferView(bytes, 34962);
                return AddAccessor(bv, 5126, n, "VEC2", null, null);
            }

            int AddJoints(ushort[] joints, int vc)
            {
                var bytes = new byte[vc * 8];
                for (int i = 0; i < vc * 4; i++) PutU16(bytes, i * 2, joints[i]);
                int bv = AddBufferView(bytes, 34962);
                return AddAccessor(bv, 5123, vc, "VEC4", null, null);
            }

            int AddWeights(float[] weights, int vc)
            {
                var bytes = new byte[vc * 16];
                for (int i = 0; i < vc * 4; i++) PutF(bytes, i * 4, weights[i]);
                int bv = AddBufferView(bytes, 34962);
                return AddAccessor(bv, 5126, vc, "VEC4", null, null);
            }

            int AddMatrices(float[] m16, int count)
            {
                var bytes = new byte[count * 64];
                for (int i = 0; i < count * 16; i++) PutF(bytes, i * 4, m16[i]);
                int bv = AddBufferView(bytes, 0);
                return AddAccessor(bv, 5126, count, "MAT4", null, null);
            }

            int AddIndicesReversed(int[] tris)
            {
                int n = tris.Length;
                var bytes = new byte[n * 4];
                // reverse winding: swap 2nd and 3rd of each triangle
                for (int t = 0; t + 2 < n; t += 3)
                {
                    PutU32(bytes, (t + 0) * 4, (uint)tris[t + 0]);
                    PutU32(bytes, (t + 1) * 4, (uint)tris[t + 2]);
                    PutU32(bytes, (t + 2) * 4, (uint)tris[t + 1]);
                }
                int bv = AddBufferView(bytes, 34963);
                return AddAccessor(bv, 5125, n, "SCALAR", null, null);
            }

            static void PutF(byte[] b, int o, float v)
            {
                var t = BitConverter.GetBytes(v);
                b[o] = t[0]; b[o + 1] = t[1]; b[o + 2] = t[2]; b[o + 3] = t[3];
            }
            static void PutU16(byte[] b, int o, ushort v) { b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)(v >> 8); }
            static void PutU32(byte[] b, int o, uint v)
            {
                b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)((v >> 8) & 0xFF);
                b[o + 2] = (byte)((v >> 16) & 0xFF); b[o + 3] = (byte)((v >> 24) & 0xFF);
            }

            // ---------- GLB container ----------

            public byte[] ToGlb()
            {
                string json = JsonConvert.SerializeObject(_root);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
                int binPad = (int)((4 - (_bin.Length % 4)) % 4);

                int jsonChunkLen = jsonBytes.Length + jsonPad;
                int binChunkLen = (int)_bin.Length + binPad;
                int total = 12 + 8 + jsonChunkLen + 8 + binChunkLen;

                var ms = new MemoryStream(total);
                var w = new BinaryWriter(ms);
                w.Write(0x46546C67u);  // "glTF"
                w.Write(2u);
                w.Write((uint)total);

                w.Write((uint)jsonChunkLen);
                w.Write(0x4E4F534Au); // "JSON"
                w.Write(jsonBytes);
                for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20); // space pad

                w.Write((uint)binChunkLen);
                w.Write(0x004E4942u); // "BIN\0"
                w.Write(_bin.ToArray());
                for (int i = 0; i < binPad; i++) w.Write((byte)0x00);

                w.Flush();
                return ms.ToArray();
            }

            static void IndexHierarchy(Transform t, Dictionary<Transform, int> index, List<Transform> order)
            {
                if (t == null) return;
                index[t] = order.Count;
                order.Add(t);
                for (int i = 0; i < t.childCount; i++)
                    IndexHierarchy(t.GetChild(i), index, order);
            }
        }

        // ---- VRM bone-name maps ----
        static readonly List<KeyValuePair<HumanBodyBones, string>> Vrm0BoneNames = BuildBoneNames();
        static readonly List<KeyValuePair<HumanBodyBones, string>> Vrm1BoneNames = Vrm0BoneNames; // same camelCase keys

        static List<KeyValuePair<HumanBodyBones, string>> BuildBoneNames()
        {
            var l = new List<KeyValuePair<HumanBodyBones, string>>();
            void A(HumanBodyBones b, string n) { l.Add(new KeyValuePair<HumanBodyBones, string>(b, n)); }
            A(HumanBodyBones.Hips, "hips"); A(HumanBodyBones.Spine, "spine"); A(HumanBodyBones.Chest, "chest");
            A(HumanBodyBones.UpperChest, "upperChest"); A(HumanBodyBones.Neck, "neck"); A(HumanBodyBones.Head, "head");
            A(HumanBodyBones.LeftEye, "leftEye"); A(HumanBodyBones.RightEye, "rightEye"); A(HumanBodyBones.Jaw, "jaw");
            A(HumanBodyBones.LeftUpperLeg, "leftUpperLeg"); A(HumanBodyBones.LeftLowerLeg, "leftLowerLeg");
            A(HumanBodyBones.LeftFoot, "leftFoot"); A(HumanBodyBones.LeftToes, "leftToes");
            A(HumanBodyBones.RightUpperLeg, "rightUpperLeg"); A(HumanBodyBones.RightLowerLeg, "rightLowerLeg");
            A(HumanBodyBones.RightFoot, "rightFoot"); A(HumanBodyBones.RightToes, "rightToes");
            A(HumanBodyBones.LeftShoulder, "leftShoulder"); A(HumanBodyBones.LeftUpperArm, "leftUpperArm");
            A(HumanBodyBones.LeftLowerArm, "leftLowerArm"); A(HumanBodyBones.LeftHand, "leftHand");
            A(HumanBodyBones.RightShoulder, "rightShoulder"); A(HumanBodyBones.RightUpperArm, "rightUpperArm");
            A(HumanBodyBones.RightLowerArm, "rightLowerArm"); A(HumanBodyBones.RightHand, "rightHand");
            A(HumanBodyBones.LeftThumbProximal, "leftThumbProximal"); A(HumanBodyBones.LeftThumbIntermediate, "leftThumbIntermediate"); A(HumanBodyBones.LeftThumbDistal, "leftThumbDistal");
            A(HumanBodyBones.LeftIndexProximal, "leftIndexProximal"); A(HumanBodyBones.LeftIndexIntermediate, "leftIndexIntermediate"); A(HumanBodyBones.LeftIndexDistal, "leftIndexDistal");
            A(HumanBodyBones.LeftMiddleProximal, "leftMiddleProximal"); A(HumanBodyBones.LeftMiddleIntermediate, "leftMiddleIntermediate"); A(HumanBodyBones.LeftMiddleDistal, "leftMiddleDistal");
            A(HumanBodyBones.LeftRingProximal, "leftRingProximal"); A(HumanBodyBones.LeftRingIntermediate, "leftRingIntermediate"); A(HumanBodyBones.LeftRingDistal, "leftRingDistal");
            A(HumanBodyBones.LeftLittleProximal, "leftLittleProximal"); A(HumanBodyBones.LeftLittleIntermediate, "leftLittleIntermediate"); A(HumanBodyBones.LeftLittleDistal, "leftLittleDistal");
            A(HumanBodyBones.RightThumbProximal, "rightThumbProximal"); A(HumanBodyBones.RightThumbIntermediate, "rightThumbIntermediate"); A(HumanBodyBones.RightThumbDistal, "rightThumbDistal");
            A(HumanBodyBones.RightIndexProximal, "rightIndexProximal"); A(HumanBodyBones.RightIndexIntermediate, "rightIndexIntermediate"); A(HumanBodyBones.RightIndexDistal, "rightIndexDistal");
            A(HumanBodyBones.RightMiddleProximal, "rightMiddleProximal"); A(HumanBodyBones.RightMiddleIntermediate, "rightMiddleIntermediate"); A(HumanBodyBones.RightMiddleDistal, "rightMiddleDistal");
            A(HumanBodyBones.RightRingProximal, "rightRingProximal"); A(HumanBodyBones.RightRingIntermediate, "rightRingIntermediate"); A(HumanBodyBones.RightRingDistal, "rightRingDistal");
            A(HumanBodyBones.RightLittleProximal, "rightLittleProximal"); A(HumanBodyBones.RightLittleIntermediate, "rightLittleIntermediate"); A(HumanBodyBones.RightLittleDistal, "rightLittleDistal");
            return l;
        }
    }
}
