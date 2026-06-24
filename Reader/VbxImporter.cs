using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using VNyanWeightStudio;   // VbxDoc / VbxIO live in the shared format namespace

namespace VNyanWeightStudioReader
{
    /// <summary>
    /// Reconstructs a full avatar GameObject hierarchy from a <see cref="VbxDoc"/>
    /// at VNyan runtime — skeleton transforms, SkinnedMeshRenderers, meshes
    /// (with an UNLIMITED number of bone influences per vertex preserved via
    /// <c>Mesh.SetBoneWeights</c>), blendshapes, and best-effort materials/textures.
    ///
    /// VBX stores everything in Unity's own coordinate space (it was written
    /// straight off live SkinnedMeshRenderers), so no axis conversion is needed on
    /// the way back in — unlike VRM/glTF import.
    /// </summary>
    public static class VbxImporter
    {
        public static GameObject Import(string path)
        {
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            {
                VbxDoc doc = VbxIO.Read(fs);
                return Build(doc);
            }
        }

        public static GameObject Build(VbxDoc doc)
        {
            var root = new GameObject(string.IsNullOrEmpty(doc.avatarName) ? "VbxAvatar" : doc.avatarName);

            // ---- rebuild skeleton ----
            var boneTransforms = new Transform[doc.bones.Count];
            for (int i = 0; i < doc.bones.Count; i++)
            {
                var b = doc.bones[i];
                // bone 0 is the avatar root itself (the exporter indexes the root first)
                Transform t = (i == 0) ? root.transform : new GameObject(b.name).transform;
                if (i != 0) t.name = b.name;
                boneTransforms[i] = t;
            }
            // parent + local TRS (parents always precede children in export order, but
            // we set parents in a second pass to be order-independent)
            for (int i = 0; i < doc.bones.Count; i++)
            {
                var b = doc.bones[i];
                Transform t = boneTransforms[i];
                if (i != 0)
                {
                    Transform parent = (b.parent >= 0 && b.parent < boneTransforms.Length)
                        ? boneTransforms[b.parent] : root.transform;
                    t.SetParent(parent, false);
                }
                t.localPosition = b.localPos;
                t.localRotation = b.localRot;
                t.localScale = b.localScale;
            }

            // ---- optional: real materials from the embedded source bundle ----
            // For a standalone load, shaders absent from this runtime (locked Poiyomi,
            // etc.) can't be reconstructed from captured properties. If the .vbx embeds
            // the original .vsfavatar bytes, load that bundle in-memory and harvest the
            // REAL compiled material instances to assign to our rebuilt meshes.
            SourceMaterialBank bank = null;
            if (PreferEmbeddedBundle && doc.embeddedBundle != null && doc.embeddedBundle.Length > 0)
                bank = SourceMaterialBank.TryLoad(doc.embeddedBundle);

            try
            {
                // ---- rebuild meshes ----
                // decoded shared-texture cache, keyed by "poolIndex:linear"
                var texCache = new Dictionary<string, Texture2D>();
                foreach (var vm in doc.meshes)
                    BuildMesh(vm, root.transform, boneTransforms, doc, texCache, bank);
            }
            finally
            {
                // Unload(false): frees the bundle container but KEEPS the loaded material/
                // shader/texture objects alive (they're now referenced by our renderers).
                if (bank != null) bank.Release();
            }

            // ---- humanoid animator (best-effort) ----
            TryBuildHumanoid(root, doc, boneTransforms);

            return root;
        }

        static void BuildMesh(VbxMesh vm, Transform root, Transform[] boneTransforms,
            VbxDoc doc, Dictionary<string, Texture2D> texCache, SourceMaterialBank bank)
        {
            var go = new GameObject(string.IsNullOrEmpty(vm.name) ? "Mesh" : vm.name);
            go.transform.SetParent(root, false);
            var smr = go.AddComponent<SkinnedMeshRenderer>();

            var mesh = new Mesh();
            mesh.name = vm.name;
            mesh.indexFormat = (vm.positions != null && vm.positions.Length > 65535)
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.vertices = vm.positions ?? new Vector3[0];
            if (vm.normals != null && vm.normals.Length == mesh.vertexCount) mesh.normals = vm.normals;
            if (vm.tangents != null && vm.tangents.Length == mesh.vertexCount) mesh.tangents = vm.tangents;
            if (vm.uv != null && vm.uv.Length == mesh.vertexCount) mesh.uv = vm.uv;
            if (vm.uv2 != null && vm.uv2.Length == mesh.vertexCount) mesh.uv2 = vm.uv2;
            if (vm.bindposes != null && vm.bindposes.Length > 0) mesh.bindposes = vm.bindposes;

            // submeshes / triangles
            int subCount = vm.submeshes != null ? vm.submeshes.Length : 0;
            mesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++)
                mesh.SetTriangles(vm.submeshes[s] ?? new int[0], s);

            // unlimited skin weights
            ApplyWeights(mesh, vm);

            // blendshapes
            if (vm.blendShapes != null)
            {
                foreach (var bs in vm.blendShapes)
                {
                    foreach (var fr in bs.frames)
                        mesh.AddBlendShapeFrame(bs.name, fr.weight, fr.dV, fr.dN, fr.dT);
                }
            }

            if (mesh.normals == null || mesh.normals.Length == 0) mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // bones
            var bones = new Transform[vm.boneMap != null ? vm.boneMap.Length : 0];
            for (int i = 0; i < bones.Length; i++)
            {
                int gi = vm.boneMap[i];
                bones[i] = (gi >= 0 && gi < boneTransforms.Length) ? boneTransforms[gi] : null;
            }

            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = (vm.rootBone >= 0 && vm.rootBone < boneTransforms.Length)
                ? boneTransforms[vm.rootBone] : (bones.Length > 0 ? bones[0] : root);
            smr.quality = SkinQuality.Auto;
            smr.updateWhenOffscreen = true;
            smr.localBounds = mesh.bounds;

            // materials: prefer the REAL compiled materials from the embedded source
            // bundle (faithful locked-Poiyomi etc.), reconstructing only what's missing.
            smr.sharedMaterials = ResolveMaterials(vm, subCount, doc, texCache, bank);
        }

        // Per-submesh material resolution. Uses the embedded source bundle's real material
        // instance for a slot when available; otherwise reconstructs from captured props.
        static Material[] ResolveMaterials(VbxMesh vm, int subCount,
            VbxDoc doc, Dictionary<string, Texture2D> texCache, SourceMaterialBank bank)
        {
            int n = Mathf.Max(subCount, 1);
            Material[] real = (bank != null) ? bank.ForMesh(vm, n) : null;

            var result = new Material[n];
            for (int i = 0; i < n; i++)
            {
                if (real != null && i < real.Length && real[i] != null)
                {
                    result[i] = real[i];
                    continue;
                }
                VbxMaterial vmat = (vm.materials != null && i < vm.materials.Count) ? vm.materials[i] : null;
                result[i] = BuildMaterial(vmat, doc, texCache);
            }
            return result;
        }

        static void ApplyWeights(Mesh mesh, VbxMesh vm)
        {
            int vc = mesh.vertexCount;
            if (vm.bonesPerVertex == null || vm.bonesPerVertex.Length != vc ||
                vm.influenceBone == null || vm.influenceWeight == null)
                return;

            int inf = vm.influenceBone.Length;
            var bpv = new NativeArray<byte>(vm.bonesPerVertex, Allocator.Temp);
            var weights = new NativeArray<BoneWeight1>(inf, Allocator.Temp);
            for (int i = 0; i < inf; i++)
                weights[i] = new BoneWeight1 { boneIndex = vm.influenceBone[i], weight = vm.influenceWeight[i] };
            try
            {
                mesh.SetBoneWeights(bpv, weights);
            }
            finally
            {
                bpv.Dispose();
                weights.Dispose();
            }

            // ensure the renderer can use >4 influences
            if (MaxOf(vm.bonesPerVertex) > 4)
                QualitySettings.skinWeights = SkinWeights.Unlimited;
        }

        static byte MaxOf(byte[] a) { byte m = 0; for (int i = 0; i < a.Length; i++) if (a[i] > m) m = a[i]; return m; }

        // ----------------------------------------------------------- materials

        // When loading a standalone avatar (no live VNyan avatar to borrow real materials
        // from), the captured shader (e.g. VRM/MToon) often can't be reconstructed into a
        // drawable state at runtime — VNyan's UniVRM build doesn't expose MToon's material
        // setup API, so a freshly-created MToon material renders nothing. In that case we
        // render with a simple Unlit material built from the base texture + color so the
        // model is at least VISIBLE (lighting/shading lost, but you can see it).
        public static bool ForceVisibleMaterials = false;

        // When true (standalone load only), prefer real materials harvested from the
        // .vbx's embedded source bundle over property-reconstruction. Set by the plugin
        // only when no live avatar is present, so we never double-load a .vsfavatar that
        // VNyan already has open (which Unity rejects with a duplicate-bundle error).
        public static bool PreferEmbeddedBundle = false;

        static Material BuildMaterial(VbxMaterial vmat, VbxDoc doc, Dictionary<string, Texture2D> texCache)
        {
            if (ForceVisibleMaterials) return BuildVisibleFallback(vmat, doc, texCache);

            // 1) Can we recreate the EXACT shader the material was authored with?
            Shader shader = null;
            if (vmat != null && !string.IsNullOrEmpty(vmat.shader))
                shader = Shader.Find(vmat.shader);

            // 2) Authored shader is missing from this runtime (e.g. Poiyomi, lilToon).
            //    Reconstructing onto a random shader by raw property name leaves it mostly
            //    default, so instead pick the closest AVAILABLE substitute and remap the
            //    captured base color / textures / emission / normal onto it. Toon shaders
            //    map to MToon (present in VNyan); everything else to Standard.
            if (shader == null)
                return BuildRemappedMaterial(vmat, doc, texCache);

            var mat = new Material(shader);
            if (vmat == null) return mat;

            mat.name = string.IsNullOrEmpty(vmat.name) ? "Material" : vmat.name;

            bool haveGeneric = (vmat.colors != null && vmat.colors.Count > 0) ||
                               (vmat.floats != null && vmat.floats.Count > 0) ||
                               (vmat.vectors != null && vmat.vectors.Count > 0) ||
                               (vmat.texProps != null && vmat.texProps.Count > 0);

            if (haveGeneric)
            {
                // v3 full reconstruction. Apply keywords first so any keyword-gated
                // properties/passes behave correctly, then push every captured value.
                // Because we also restore the captured render-state floats (_SrcBlend,
                // _DstBlend, _ZWrite, _BlendMode, _Cull, ...) and renderQueue, MToon
                // reconstructs into the correct drawable state WITHOUT needing MToon's
                // own ValidateProperties setup API (which VNyan doesn't expose).
                if (vmat.keywords != null)
                    foreach (var kw in vmat.keywords)
                        if (!string.IsNullOrEmpty(kw)) mat.EnableKeyword(kw);

                if (vmat.floats != null)
                    foreach (var f in vmat.floats)
                        if (mat.HasProperty(f.name)) mat.SetFloat(f.name, f.value);

                if (vmat.colors != null)
                    foreach (var c in vmat.colors)
                        if (mat.HasProperty(c.name)) mat.SetColor(c.name, c.value);

                if (vmat.vectors != null)
                    foreach (var v in vmat.vectors)
                        if (mat.HasProperty(v.name)) mat.SetVector(v.name, v.value);

                if (vmat.texProps != null)
                {
                    foreach (var tp in vmat.texProps)
                    {
                        if (!mat.HasProperty(tp.name)) continue;
                        if (tp.texIndex >= 0)
                        {
                            bool linear = IsLinearProp(tp.name);
                            Texture2D tex = DecodePooled(doc, texCache, tp.texIndex, linear);
                            if (tex != null) mat.SetTexture(tp.name, tex);
                        }
                        mat.SetTextureScale(tp.name, tp.scale);
                        mat.SetTextureOffset(tp.name, tp.offset);
                    }
                }

                if (vmat.renderQueue >= 0) mat.renderQueue = vmat.renderQueue;
                PostProcessShader(mat);
                return mat;
            }

            // ---- legacy (v1/v2) fallback path ----
            SetColor(mat, "_Color", vmat.color);
            SetColor(mat, "_BaseColor", vmat.color);
            if (vmat.emission != Color.black)
            {
                SetColor(mat, "_EmissionColor", vmat.emission);
                mat.EnableKeyword("_EMISSION");
            }
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", vmat.cutoff);
            if (vmat.renderQueue >= 0) mat.renderQueue = vmat.renderQueue;

            Texture2D main = PngToTexture(vmat.mainTexPng, false);
            if (main != null) { SetTex(mat, "_MainTex", main); SetTex(mat, "_BaseMap", main); }
            Texture2D normal = PngToTexture(vmat.normalTexPng, true);
            if (normal != null) { SetTex(mat, "_BumpMap", normal); SetTex(mat, "_NormalMap", normal); mat.EnableKeyword("_NORMALMAP"); }
            Texture2D emis = PngToTexture(vmat.emissionTexPng, false);
            if (emis != null) SetTex(mat, "_EmissionMap", emis);

            PostProcessShader(mat);
            return mat;
        }

        // The captured shader isn't present in this runtime. Build the closest available
        // substitute (MToon for toon shaders such as Poiyomi/lilToon; Standard otherwise)
        // and remap the captured base color, main/normal/emission textures and the
        // opaque/cutout/transparent render mode onto it by property-name heuristics.
        static Material BuildRemappedMaterial(VbxMaterial vmat, VbxDoc doc, Dictionary<string, Texture2D> texCache)
        {
            bool toon = LooksToon(vmat != null ? vmat.shader : null);

            Shader sh = null;
            if (toon)
            {
                sh = Shader.Find("VRM/MToon");
                if (sh == null) sh = Shader.Find("Standard");
            }
            else
            {
                sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("VRM/MToon");
            }
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Unlit/Texture");

            var mat = new Material(sh);
            if (vmat == null) return mat;
            bool isMToon = sh.name == "VRM/MToon";
            mat.name = (string.IsNullOrEmpty(vmat.name) ? "Material" : vmat.name) +
                       " (→" + sh.name + ")";

            // --- base color ---
            Color baseColor = FindColor(vmat, "_color", "_basecolor", "_maincolor");
            if (baseColor.a <= 0f && !IsTransparentLike(vmat)) baseColor.a = 1f;
            SetColor(mat, "_Color", baseColor);
            SetColor(mat, "_BaseColor", baseColor);
            if (isMToon) SetColor(mat, "_ShadeColor", baseColor * 0.6f);

            // --- main / albedo texture ---
            Texture2D main = FindTexByHints(vmat, doc, texCache, false,
                "_maintex", "_basemap", "_basecolormap", "maintex", "albedo", "basecolor");
            if (main != null)
            {
                SetTex(mat, "_MainTex", main);
                SetTex(mat, "_BaseMap", main);
                if (isMToon) SetTex(mat, "_ShadeTexture", main);  // shade region textured too
            }

            // --- normal map ---
            Texture2D normal = FindTexByHints(vmat, doc, texCache, true,
                "_bumpmap", "_normalmap", "bump", "normal");
            if (normal != null)
            {
                SetTex(mat, "_BumpMap", normal);
                SetTex(mat, "_NormalMap", normal);
                mat.EnableKeyword("_NORMALMAP");
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
            }

            // --- emission ---
            Texture2D emis = FindTexByHints(vmat, doc, texCache, false, "_emissionmap", "emission");
            Color emisColor = FindColor(vmat, "_emissioncolor", "_emissivecolor");
            if (emis != null || (emisColor.maxColorComponent > 0.001f))
            {
                if (emis != null) SetTex(mat, "_EmissionMap", emis);
                if (emisColor.maxColorComponent > 0.001f) SetColor(mat, "_EmissionColor", emisColor);
                else SetColor(mat, "_EmissionColor", Color.white);
                mat.EnableKeyword("_EMISSION");
                if (mat.HasProperty("_EmissionEnable")) mat.SetFloat("_EmissionEnable", 1f);
            }

            // --- render mode (opaque / cutout / transparent) ---
            ApplyRenderMode(mat, vmat, isMToon);
            return mat;
        }

        // Classify the captured render mode and configure the substitute shader's
        // blend/zwrite/queue/keywords accordingly.
        static void ApplyRenderMode(Material mat, VbxMaterial vmat, bool isMToon)
        {
            float cutoff = FindFloat(vmat, 0.5f, "_cutoff");
            bool hasCutoffProp = HasFloatProp(vmat, "_cutoff");
            bool kwCutout = HasKeyword(vmat, "_ALPHATEST_ON");
            bool kwBlend = HasKeyword(vmat, "_ALPHABLEND_ON") || HasKeyword(vmat, "_ALPHAPREMULTIPLY_ON");
            int rq = vmat.renderQueue;

            bool transparent = kwBlend || rq >= 3000;
            bool cutout = !transparent && (kwCutout || (hasCutoffProp && rq >= 2450 && rq < 3000));

            if (transparent)
            {
                if (isMToon && mat.HasProperty("_BlendMode")) mat.SetFloat("_BlendMode", 2f);
                if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.renderQueue = rq >= 3000 ? rq : (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else if (cutout)
            {
                if (isMToon && mat.HasProperty("_BlendMode")) mat.SetFloat("_BlendMode", 1f);
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", cutoff);
                if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            else
            {
                if (isMToon && mat.HasProperty("_BlendMode")) mat.SetFloat("_BlendMode", 0f);
                if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            }
        }

        static bool LooksToon(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            string n = shaderName.ToLowerInvariant();
            return n.Contains("poiyomi") || n.Contains("toon") || n.Contains("mtoon") ||
                   n.Contains("liltoon") || n.Contains("lil/") || n.Contains("cel") ||
                   n.Contains("unitychan") || n.Contains("arktoon");
        }

        static bool IsTransparentLike(VbxMaterial vmat)
        {
            return vmat != null && (vmat.renderQueue >= 3000 ||
                   HasKeyword(vmat, "_ALPHABLEND_ON") || HasKeyword(vmat, "_ALPHAPREMULTIPLY_ON"));
        }

        static Color FindColor(VbxMaterial vmat, params string[] lowerNames)
        {
            if (vmat != null && vmat.colors != null)
                foreach (var hint in lowerNames)
                    foreach (var c in vmat.colors)
                        if (c != null && c.name != null && c.name.ToLowerInvariant() == hint)
                            return c.value;
            return Color.white;
        }

        static float FindFloat(VbxMaterial vmat, float fallback, params string[] lowerNames)
        {
            if (vmat != null && vmat.floats != null)
                foreach (var hint in lowerNames)
                    foreach (var f in vmat.floats)
                        if (f != null && f.name != null && f.name.ToLowerInvariant() == hint)
                            return f.value;
            return fallback;
        }

        static bool HasFloatProp(VbxMaterial vmat, string lowerName)
        {
            if (vmat != null && vmat.floats != null)
                foreach (var f in vmat.floats)
                    if (f != null && f.name != null && f.name.ToLowerInvariant() == lowerName) return true;
            return false;
        }

        static bool HasKeyword(VbxMaterial vmat, string kw)
        {
            if (vmat != null && vmat.keywords != null)
                foreach (var k in vmat.keywords)
                    if (k == kw) return true;
            return false;
        }

        // Find a captured texture property whose (lowercased) name matches an exact hint
        // first, then a 'contains' hint, and decode it from the shared pool.
        static Texture2D FindTexByHints(VbxMaterial vmat, VbxDoc doc,
            Dictionary<string, Texture2D> texCache, bool linear, params string[] hints)
        {
            if (vmat == null || vmat.texProps == null || vmat.texProps.Count == 0) return null;

            // exact match pass
            foreach (var hint in hints)
                foreach (var tp in vmat.texProps)
                    if (tp != null && tp.name != null && tp.texIndex >= 0 &&
                        tp.name.ToLowerInvariant() == hint)
                        return DecodePooled(doc, texCache, tp.texIndex, linear);

            // contains pass
            foreach (var hint in hints)
                foreach (var tp in vmat.texProps)
                    if (tp != null && tp.name != null && tp.texIndex >= 0 &&
                        tp.name.ToLowerInvariant().Contains(hint))
                        return DecodePooled(doc, texCache, tp.texIndex, linear);

            return null;
        }

        // Build a guaranteed-visible unlit material from a captured material's base texture
        // and color. Used for standalone loads where the real shader can't be reconstructed.
        static Material BuildVisibleFallback(VbxMaterial vmat, VbxDoc doc, Dictionary<string, Texture2D> texCache)
        {
            Shader sh = Shader.Find("Unlit/Texture");
            bool unlitTextured = sh != null;
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Standard");
            var mat = new Material(sh);
            if (vmat == null) return mat;
            mat.name = (string.IsNullOrEmpty(vmat.name) ? "Material" : vmat.name) + " (unlit)";

            // pick a base/albedo texture from the captured texture properties
            int texIdx = -1;
            if (vmat.texProps != null && vmat.texProps.Count > 0)
            {
                foreach (var tp in vmat.texProps)
                {
                    if (tp == null || tp.name == null) continue;
                    string n = tp.name.ToLowerInvariant();
                    if (n == "_maintex" || n == "_basemap" || n.Contains("basecolor") ||
                        n.Contains("maintex") || n.Contains("albedo"))
                    { texIdx = tp.texIndex; break; }
                }
                if (texIdx < 0) texIdx = vmat.texProps[0].texIndex;   // first texture as a last resort
            }
            if (texIdx >= 0)
            {
                Texture2D tex = DecodePooled(doc, texCache, texIdx, false);
                if (tex != null)
                {
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                }
            }

            // base color tint (Unlit/Texture has no _Color; Unlit/Color + Standard do)
            Color tint = Color.white;
            if (vmat.colors != null)
                foreach (var c in vmat.colors)
                    if (c != null && (c.name == "_Color" || c.name == "_BaseColor")) { tint = c.value; break; }
            tint.a = 1f;   // force opaque so nothing vanishes from a 0 alpha
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", unlitTextured ? Color.white : tint);

            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;  // opaque, no transparency surprises
            return mat;
        }

        // Some shaders (notably VRM/MToon) carry fixed-function render state — blend mode,
        // ZWrite, cull, stencil, outline, keyword gating — that is NOT configured just by
        // setting their float/color/texture properties. The shader's own setup routine has
        // to push those values into the material's render state, or it renders wrong (often
        // fully invisible). UniVRM is loaded in VNyan, so call MToon's own
        // MToon.Utils.ValidateProperties(material, isEditor:false) via reflection.
        static bool _mtoonResolved;
        static System.Reflection.MethodInfo _mtoonValidate;

        static void PostProcessShader(Material mat)
        {
            if (mat == null || mat.shader == null) return;
            if (mat.shader.name == "VRM/MToon")
            {
                var mi = GetMToonValidate();
                if (mi != null)
                {
                    try { mi.Invoke(null, new object[] { mat, false }); }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning("[WeightStudioReader] MToon ValidateProperties failed: " + e.Message);
                    }
                }
                else
                {
                    Debug.LogWarning("[WeightStudioReader] MToon.Utils.ValidateProperties not found; " +
                                     "MToon material render state may be wrong (could appear invisible).");
                }
            }
        }

        static System.Reflection.MethodInfo GetMToonValidate()
        {
            if (_mtoonResolved) return _mtoonValidate;
            _mtoonResolved = true;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = null;
                try { t = asm.GetType("MToon.Utils"); } catch { }
                if (t == null) continue;
                _mtoonValidate = t.GetMethod("ValidateProperties",
                    new System.Type[] { typeof(Material), typeof(bool) });
                if (_mtoonValidate != null) break;
            }
            return _mtoonValidate;
        }

        /// <summary>Heuristic: normal/bump/detail-normal maps are sampled in linear space.</summary>
        static bool IsLinearProp(string prop)
        {
            if (string.IsNullOrEmpty(prop)) return false;
            string p = prop.ToLowerInvariant();
            return p.Contains("bump") || p.Contains("normal");
        }

        static Texture2D DecodePooled(VbxDoc doc, Dictionary<string, Texture2D> cache, int index, bool linear)
        {
            if (doc.textures == null || index < 0 || index >= doc.textures.Count) return null;
            string key = index + ":" + (linear ? "1" : "0");
            Texture2D tex;
            if (cache.TryGetValue(key, out tex)) return tex;
            tex = PngToTexture(doc.textures[index], linear);
            cache[key] = tex;
            return tex;
        }

        static void SetColor(Material m, string prop, Color c) { if (m.HasProperty(prop)) m.SetColor(prop, c); }
        static void SetTex(Material m, string prop, Texture t) { if (m.HasProperty(prop)) m.SetTexture(prop, t); }

        static Texture2D PngToTexture(byte[] png, bool linear)
        {
            if (png == null || png.Length == 0) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
            if (!tex.LoadImage(png)) { Object.Destroy(tex); return null; }
            tex.wrapMode = TextureWrapMode.Repeat;
            return tex;
        }

        // ------------------------------------------------- embedded source-bundle materials

        // Loads an embedded source AssetBundle (.vsfavatar bytes) in-memory and indexes its
        // real, fully-compiled materials so they can be assigned to the rebuilt meshes —
        // giving faithful results for shaders that don't exist in this runtime (locked
        // Poiyomi, lilToon, …). Match priority: renderer name (the exporter sets
        // VbxMesh.name = SkinnedMeshRenderer.name) + submesh order; then material name.
        sealed class SourceMaterialBank
        {
            AssetBundle _bundle;
            readonly Dictionary<string, Material[]> _byRenderer =
                new Dictionary<string, Material[]>();
            readonly Dictionary<string, Material> _byName =
                new Dictionary<string, Material>();

            public static SourceMaterialBank TryLoad(byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0) return null;
                AssetBundle b = null;
                try
                {
                    b = AssetBundle.LoadFromMemory(bytes);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[WeightStudioReader] Embedded bundle load failed: " + e.Message);
                    return null;
                }
                if (b == null)
                {
                    Debug.LogWarning("[WeightStudioReader] Embedded bundle returned null (already loaded, or not a bundle).");
                    return null;
                }

                var bank = new SourceMaterialBank { _bundle = b };
                try
                {
                    foreach (var go in b.LoadAllAssets<GameObject>())
                    {
                        if (go == null) continue;
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            if (r == null || r.name == null) continue;
                            Material[] sm = r.sharedMaterials;
                            if (sm != null && sm.Length > 0 && !bank._byRenderer.ContainsKey(r.name))
                                bank._byRenderer[r.name] = sm;
                        }
                    }
                    foreach (var m in b.LoadAllAssets<Material>())
                        if (m != null && m.name != null && !bank._byName.ContainsKey(m.name))
                            bank._byName[m.name] = m;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[WeightStudioReader] Embedded bundle index failed: " + e.Message);
                }

                if (bank._byRenderer.Count == 0 && bank._byName.Count == 0)
                {
                    Debug.LogWarning("[WeightStudioReader] Embedded bundle had no usable materials.");
                    bank.Release();
                    return null;
                }
                return bank;
            }

            // Resolve a material array for one mesh; entries may be null if unmatched.
            public Material[] ForMesh(VbxMesh vm, int subCount)
            {
                // 1) exact renderer-name match -> its sharedMaterials, fitted to submeshes
                Material[] src;
                if (vm != null && vm.name != null &&
                    _byRenderer.TryGetValue(vm.name, out src) && src != null && src.Length > 0)
                {
                    var fit = new Material[subCount];
                    for (int i = 0; i < subCount; i++)
                        fit[i] = (i < src.Length) ? src[i] : src[src.Length - 1];
                    return fit;
                }

                // 2) fall back to matching each captured material by name
                if (vm != null && vm.materials != null && vm.materials.Count > 0)
                {
                    var byName = new Material[subCount];
                    bool any = false;
                    for (int i = 0; i < subCount; i++)
                    {
                        VbxMaterial vmat = (i < vm.materials.Count) ? vm.materials[i] : null;
                        Material mm = null;
                        if (vmat != null && vmat.name != null) _byName.TryGetValue(vmat.name, out mm);
                        byName[i] = mm;
                        if (mm != null) any = true;
                    }
                    if (any) return byName;
                }
                return null;
            }

            public void Release()
            {
                if (_bundle != null) { _bundle.Unload(false); _bundle = null; }
            }
        }

        // ----------------------------------------------------------- humanoid

        static void TryBuildHumanoid(GameObject root, VbxDoc doc, Transform[] boneTransforms)
        {
            var animator = root.AddComponent<Animator>();
            if (doc.humanoid == null || doc.humanoid.Count == 0) return;

            try
            {
                string[] traitNames = HumanTrait.BoneName;   // indexed by human bone id

                var humanBones = new List<HumanBone>();
                foreach (var hb in doc.humanoid)
                {
                    if (hb.humanBone < 0 || hb.humanBone >= traitNames.Length) continue;
                    if (hb.boneIndex < 0 || hb.boneIndex >= boneTransforms.Length) continue;
                    Transform t = boneTransforms[hb.boneIndex];
                    if (t == null) continue;
                    humanBones.Add(new HumanBone
                    {
                        humanName = traitNames[hb.humanBone],
                        boneName = t.name,
                        limit = new HumanLimit { useDefaultValues = true }
                    });
                }
                if (humanBones.Count == 0) return;

                var skeleton = new List<SkeletonBone>();
                for (int i = 0; i < doc.bones.Count; i++)
                {
                    var b = doc.bones[i];
                    skeleton.Add(new SkeletonBone
                    {
                        name = boneTransforms[i].name,
                        position = b.localPos,
                        rotation = b.localRot,
                        scale = b.localScale
                    });
                }

                var desc = new HumanDescription
                {
                    human = humanBones.ToArray(),
                    skeleton = skeleton.ToArray(),
                    upperArmTwist = 0.5f,
                    lowerArmTwist = 0.5f,
                    upperLegTwist = 0.5f,
                    lowerLegTwist = 0.5f,
                    armStretch = 0.05f,
                    legStretch = 0.05f,
                    feetSpacing = 0f,
                    hasTranslationDoF = false
                };

                Avatar avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
                if (avatar != null && avatar.isValid)
                {
                    avatar.name = root.name + "_Avatar";
                    animator.avatar = avatar;
                }
                else
                {
                    Debug.LogWarning("[WeightStudioReader] Humanoid avatar invalid; left as Generic.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[WeightStudioReader] Humanoid rebuild failed: " + e.Message);
            }
        }
    }
}
