using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Builds a <see cref="VbxDoc"/> from a loaded avatar (reading the live, possibly
    /// painted, mesh data straight off each SkinnedMeshRenderer) and writes it to a
    /// .vbx file. Reads whatever influence count the mesh currently has, so &gt;4
    /// bones/vertex survive the round-trip.
    ///
    /// Call AFTER WeightModel.ApplyToMesh() so the painted weights are on the mesh.
    /// </summary>
    public static class VbxExporter
    {
        public static void Export(string path, Transform avatarRoot, IList<SkinnedMeshRenderer> renderers, Animator animator,
            IList<ColliderGen.GenCollider> colliders = null, string embeddedBundlePath = null)
        {
            VbxDoc doc = Build(avatarRoot, renderers, animator, colliders);

            // v4: optionally embed the source AssetBundle (.vsfavatar) so the Reader can
            // recover the REAL compiled materials (locked Poiyomi, etc.) for shaders that
            // don't exist in the load-time runtime. Costs file size (the source bundle's
            // geometry/textures are duplicated) but makes the .vbx truly self-contained.
            if (!string.IsNullOrEmpty(embeddedBundlePath))
            {
                try { doc.embeddedBundle = File.ReadAllBytes(embeddedBundlePath); }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[WeightStudio] Couldn't embed source bundle '" +
                                     embeddedBundlePath + "': " + e.Message);
                }
            }

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                VbxIO.Write(fs, doc);
        }

        public static VbxDoc Build(Transform avatarRoot, IList<SkinnedMeshRenderer> renderers, Animator animator,
            IList<ColliderGen.GenCollider> colliders = null)
        {
            var doc = new VbxDoc();
            doc.avatarName = avatarRoot != null ? avatarRoot.name : "avatar";

            // index every transform under the root so the full skeleton is captured
            var index = new Dictionary<Transform, int>();
            var order = new List<Transform>();
            if (avatarRoot != null) IndexHierarchy(avatarRoot, index, order);

            foreach (var t in order)
            {
                // NOTE: do NOT call index.TryGetValue(t.parent, ...) here: the avatar root
                // has a null parent, and Dictionary.TryGetValue(null) throws ArgumentNullException
                // ("Value cannot be null. Parameter name: key"). The parent index is computed
                // safely below with an explicit null guard.
                doc.bones.Add(new VbxBone
                {
                    name = t.name,
                    parent = (t.parent != null && index.ContainsKey(t.parent)) ? index[t.parent] : -1,
                    localPos = t.localPosition,
                    localRot = t.localRotation,
                    localScale = t.localScale
                });
            }

            // humanoid map
            if (animator != null && animator.isHuman)
            {
                for (int hb = 0; hb < (int)HumanBodyBones.LastBone; hb++)
                {
                    Transform bt = animator.GetBoneTransform((HumanBodyBones)hb);
                    if (bt != null && index.TryGetValue(bt, out int bi))
                        doc.humanoid.Add(new VbxHumanBone { humanBone = hb, boneIndex = bi });
                }
            }

            // shared texture pool: encode each unique Texture once, reference by index.
            var texPool = new Dictionary<Texture, int>();

            foreach (var smr in renderers)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                var vm = BuildMesh(smr, index, doc, texPool);
                if (vm != null) doc.meshes.Add(vm);
            }

            // colliders (resolve attachment bone by name -> global bone index)
            if (colliders != null && colliders.Count > 0)
            {
                var nameToIndex = new Dictionary<string, int>();
                for (int i = 0; i < order.Count; i++)
                    if (!nameToIndex.ContainsKey(order[i].name)) nameToIndex[order[i].name] = i;

                foreach (var c in colliders)
                {
                    if (c == null) continue;
                    int bi;
                    if (string.IsNullOrEmpty(c.boneName) || !nameToIndex.TryGetValue(c.boneName, out bi)) bi = -1;
                    doc.colliders.Add(new VbxCollider
                    {
                        name = c.name,
                        boneName = c.boneName,
                        boneIndex = bi,
                        shape = (int)c.shape,   // 0 sphere, 1 capsule
                        center = c.center,
                        centerEnd = c.centerEnd,
                        axis = c.Axis,
                        radius = c.radius,
                        height = c.Height
                    });
                }
            }
            return doc;
        }

        static void IndexHierarchy(Transform t, Dictionary<Transform, int> index, List<Transform> order)
        {
            index[t] = order.Count;
            order.Add(t);
            for (int i = 0; i < t.childCount; i++)
                IndexHierarchy(t.GetChild(i), index, order);
        }

        static VbxMesh BuildMesh(SkinnedMeshRenderer smr, Dictionary<Transform, int> index,
            VbxDoc doc, Dictionary<Texture, int> texPool)
        {
            Mesh mesh = smr.sharedMesh;
            if (!mesh.isReadable)
            {
                Debug.LogWarning("[WeightStudio] Skipping unreadable mesh '" + mesh.name + "' during export.");
                return null;
            }

            var vm = new VbxMesh();
            vm.name = smr.name;

            // bone map (renderer slot -> global bone index)
            Transform[] bones = smr.bones;
            vm.boneMap = new int[bones != null ? bones.Length : 0];
            for (int i = 0; i < vm.boneMap.Length; i++)
                vm.boneMap[i] = (bones[i] != null && index.TryGetValue(bones[i], out int bi)) ? bi : -1;
            vm.rootBone = (smr.rootBone != null && index.TryGetValue(smr.rootBone, out int rb)) ? rb : -1;

            vm.positions = mesh.vertices;
            vm.normals = mesh.normals;
            vm.tangents = mesh.tangents;
            vm.uv = mesh.uv;
            vm.uv2 = mesh.uv2;
            vm.bindposes = mesh.bindposes;

            // unlimited weights
            var bpv = mesh.GetBonesPerVertex();
            var flat = mesh.GetAllBoneWeights();
            vm.bonesPerVertex = bpv.Length > 0 ? bpv.ToArray() : new byte[mesh.vertexCount];
            int inf = flat.Length;
            vm.influenceBone = new int[inf];
            vm.influenceWeight = new float[inf];
            for (int i = 0; i < inf; i++)
            {
                vm.influenceBone[i] = flat[i].boneIndex;
                vm.influenceWeight[i] = flat[i].weight;
            }

            // submeshes
            vm.submeshes = new int[mesh.subMeshCount][];
            for (int s = 0; s < mesh.subMeshCount; s++)
                vm.submeshes[s] = mesh.GetTriangles(s);

            // blendshapes
            int vc = mesh.vertexCount;
            for (int b = 0; b < mesh.blendShapeCount; b++)
            {
                var bs = new VbxBlendShape { name = mesh.GetBlendShapeName(b) };
                int frames = mesh.GetBlendShapeFrameCount(b);
                for (int f = 0; f < frames; f++)
                {
                    var dV = new Vector3[vc];
                    var dN = new Vector3[vc];
                    var dT = new Vector3[vc];
                    mesh.GetBlendShapeFrameVertices(b, f, dV, dN, dT);
                    bs.frames.Add(new VbxBlendFrame
                    {
                        weight = mesh.GetBlendShapeFrameWeight(b, f),
                        dV = dV, dN = dN, dT = dT
                    });
                }
                vm.blendShapes.Add(bs);
            }

            // materials (full generic capture, v3)
            Material[] mats = smr.sharedMaterials;
            foreach (var mat in mats)
                vm.materials.Add(BuildMaterial(mat, doc, texPool));

            return vm;
        }

        static VbxMaterial BuildMaterial(Material mat, VbxDoc doc, Dictionary<Texture, int> texPool)
        {
            var vmat = new VbxMaterial();
            if (mat == null) { vmat.name = "null"; vmat.shader = "Standard"; return vmat; }

            vmat.name = mat.name;
            Shader sh = mat.shader;
            vmat.shader = sh != null ? sh.name : "Standard";
            vmat.renderQueue = mat.renderQueue;

            // legacy best-effort fields (fallback for when the shader is missing on load)
            if (mat.HasProperty("_Color")) vmat.color = mat.GetColor("_Color");
            else if (mat.HasProperty("_BaseColor")) vmat.color = mat.GetColor("_BaseColor");
            if (mat.HasProperty("_Cutoff")) vmat.cutoff = mat.GetFloat("_Cutoff");
            if (mat.HasProperty("_EmissionColor")) vmat.emission = mat.GetColor("_EmissionColor");
            vmat.mainTexPng = null;     // textures now live only in the shared pool
            vmat.normalTexPng = null;
            vmat.emissionTexPng = null;

            // enabled keywords
            vmat.keywords = mat.shaderKeywords != null ? (string[])mat.shaderKeywords.Clone() : new string[0];

            // enumerate every shader property and snapshot its current value
            if (sh != null)
            {
                int pc = sh.GetPropertyCount();
                for (int i = 0; i < pc; i++)
                {
                    string pname = sh.GetPropertyName(i);
                    var ptype = sh.GetPropertyType(i);
                    switch (ptype)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            vmat.colors.Add(new VbxMatColor { name = pname, value = mat.GetColor(pname) });
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                            vmat.vectors.Add(new VbxMatVector { name = pname, value = mat.GetVector(pname) });
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            vmat.floats.Add(new VbxMatFloat { name = pname, value = mat.GetFloat(pname) });
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Int:
                            vmat.floats.Add(new VbxMatFloat { name = pname, value = mat.GetInt(pname) });
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            Texture tex = mat.GetTexture(pname);
                            var vt = new VbxMatTexture
                            {
                                name = pname,
                                texIndex = PoolTexture(tex, doc, texPool),
                                scale = mat.GetTextureScale(pname),
                                offset = mat.GetTextureOffset(pname)
                            };
                            vmat.texProps.Add(vt);
                            break;
                    }
                }
            }
            return vmat;
        }

        /// <summary>Encode a texture into the shared doc pool, returning its index (-1 if none).</summary>
        static int PoolTexture(Texture tex, VbxDoc doc, Dictionary<Texture, int> texPool)
        {
            if (tex == null) return -1;
            int idx;
            if (texPool.TryGetValue(tex, out idx)) return idx;
            byte[] png = TextureToPng(tex);
            if (png == null) { texPool[tex] = -1; return -1; }
            idx = doc.textures.Count;
            doc.textures.Add(png);
            texPool[tex] = idx;
            return idx;
        }

        /// <summary>Encode any texture to PNG, blitting through a RenderTexture if it isn't CPU-readable.</summary>
        public static byte[] TextureToPng(Texture tex)
        {
            if (tex == null) return null;
            Texture2D t2d = tex as Texture2D;
            if (t2d != null && t2d.isReadable)
            {
                try { return t2d.EncodeToPNG(); } catch { /* fall through to blit */ }
            }

            int w = tex.width, h = tex.height;
            if (w <= 0 || h <= 0) return null;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply();
                byte[] png = readable.EncodeToPNG();
                Object.Destroy(readable);
                return png;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[WeightStudio] Texture encode failed: " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
