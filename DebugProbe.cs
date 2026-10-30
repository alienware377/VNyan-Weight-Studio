using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Developer self-test, idle unless a trigger file appears:
    ///   %LocalLow%\Suvidriel\VNyan\WeightStudio_probe.txt   (content = renderer name; empty = the mesh selected in Weight Studio)
    /// It reports the renderer's real state (mesh, shape keys, materials and their depth settings,
    /// children/proxies), compares its skinned shape with an overlay copy, and renders the main
    /// camera off-screen with test overlays in several render setups, counting how many overlay
    /// pixels survive. Output: WeightStudio\probe\report.txt + PNGs. Nothing is left behind.
    /// </summary>
    public partial class BoneWeightStudioPlugin
    {
        float lastProbePoll;
        bool probeRunning;

        void ProbeTick()
        {
            if (probeRunning || Time.unscaledTime - lastProbePoll < 2f) return;
            lastProbePoll = Time.unscaledTime;
            string trig = Path.Combine(Application.persistentDataPath, "WeightStudio_probe.txt");
            if (!File.Exists(trig)) return;
            string target = activeRenderer != null ? activeRenderer.name : "";
            try { string s = File.ReadAllText(trig).Trim(); if (s.Length > 0) target = s; File.Delete(trig); } catch { }
            if (target.Length == 0) { Debug.Log("[WeightStudio][probe] no target: select a mesh, or write a renderer name into the trigger file"); return; }
            probeRunning = true;
            if (target.StartsWith("heatmap:")) StartCoroutine(ProbeHeatmap(target.Substring(8).Trim()));
            else if (target.StartsWith("phases:")) StartCoroutine(ProbePhases(target.Substring(7).Trim()));
            else if (target == "windows") StartCoroutine(ProbeWindows());
            else StartCoroutine(Probe(target));
        }

        IEnumerator Probe(string target)
        {
            string dir = Path.Combine(Application.persistentDataPath, "WeightStudio", "probe");
            var sb = new StringBuilder();
            var temps = new List<Object>();
            try { Directory.CreateDirectory(dir); } catch { }
            sb.AppendLine("probe target '" + target + "' frame " + Time.frameCount);

            SkinnedMeshRenderer b = null;
            foreach (SkinnedMeshRenderer s in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
            {
                if (s == null || s.name != target || !s.gameObject.scene.IsValid()) continue;
                DescribeRenderer(s, sb);
                if (b == null || (s.isVisible && !b.isVisible)) b = s;
            }
            sb.AppendLine("WS activeRenderer=" + (activeRenderer != null ? activeRenderer.name + "#" + activeRenderer.GetInstanceID() : "null") +
                          " model.workingMesh=" + (model != null && model.workingMesh != null ? model.workingMesh.name + "#" + model.workingMesh.GetInstanceID() : "null") +
                          " heatmap.Enabled=" + heatmap.Enabled +
                          " overlay=" + (heatmap.OverlayRenderer != null ? heatmap.OverlayRenderer.sharedMesh.name + "#" + heatmap.OverlayRenderer.sharedMesh.GetInstanceID() : "none"));
            if (b == null)
            {
                sb.AppendLine("no renderer named '" + target + "'");
                Finish(dir, sb, temps);
                yield break;
            }

            // ---- geometry: base vs an overlay copy (same mesh, bones, shape keys)
            Mesh testMesh = Object.Instantiate(b.sharedMesh);
            testMesh.name = "WS_ProbeMesh";
            temps.Add(testMesh);
            var cols = new Color32[testMesh.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(255, 0, 255, 255);
            testMesh.colors32 = cols;
            GameObject go = new GameObject("WeightStudio_ProbeOverlay");
            temps.Add(go);
            go.transform.SetParent(b.transform, false);
            SkinnedMeshRenderer t = go.AddComponent<SkinnedMeshRenderer>();
            t.sharedMesh = testMesh;
            t.bones = b.bones;
            t.rootBone = b.rootBone;
            t.quality = b.quality;
            t.updateWhenOffscreen = true;
            t.localBounds = b.localBounds;
            t.shadowCastingMode = ShadowCastingMode.Off;
            t.receiveShadows = false;
            t.enabled = false;
            for (int k = 0; k < testMesh.blendShapeCount; k++) t.SetBlendShapeWeight(k, b.GetBlendShapeWeight(k));
            yield return null;
            yield return null;

            Mesh bakeB = new Mesh(), bakeT = new Mesh();
            temps.Add(bakeB); temps.Add(bakeT);
            for (int k = 0; k < testMesh.blendShapeCount; k++) t.SetBlendShapeWeight(k, b.GetBlendShapeWeight(k));
            b.BakeMesh(bakeB, true);
            t.BakeMesh(bakeT, true);
            Vector3[] vb = bakeB.vertices, vt = bakeT.vertices;
            if (vb.Length == vt.Length && vb.Length > 0)
            {
                double sum = 0; float max = 0f; int maxAt = 0;
                for (int i = 0; i < vb.Length; i++)
                {
                    float d = (vb[i] - vt[i]).magnitude;
                    sum += d; if (d > max) { max = d; maxAt = i; }
                }
                sb.AppendLine("bake base vs overlay copy: verts=" + vb.Length + " mean=" + (sum / vb.Length).ToString("G4") + " max=" + max.ToString("G4") + " at v" + maxAt);
            }
            else sb.AppendLine("bake vertex counts differ: base " + vb.Length + " overlay " + vt.Length);
            // what the unskinned bind pose gives vs the baked base (shows bone-driven size changes)
            sb.AppendLine("base baked bounds " + bakeB.bounds.center.ToString("F4") + " size " + bakeB.bounds.size.ToString("F4") +
                          " | mesh bounds " + b.sharedMesh.bounds.size.ToString("F4"));

            // ---- visual: render the main camera off-screen with each overlay setup
            EnsureCamera();
            if (cam == null) { sb.AppendLine("no camera"); Finish(dir, sb, temps); yield break; }
            sb.AppendLine("camera " + cam.name + " " + cam.pixelWidth + "x" + cam.pixelHeight + " hdr=" + cam.allowHDR + " path=" + cam.actualRenderingPath + " depthTexMode=" + cam.depthTextureMode);
            Rect area = ScreenRect(b.bounds);
            sb.AppendLine("renderer screen rect " + area);

            var setups = new List<KeyValuePair<string, Material>>();
            setups.Add(new KeyValuePair<string, Material>("none", null));
            setups.Add(new KeyValuePair<string, Material>("spritesDefault", MakeSprites(temps)));
            setups.Add(new KeyValuePair<string, Material>("colored_bias0", MakeColored(temps, 0f, CompareFunction.LessEqual)));
            setups.Add(new KeyValuePair<string, Material>("colored_bias-1", MakeColored(temps, -1f, CompareFunction.LessEqual)));
            setups.Add(new KeyValuePair<string, Material>("colored_bias-4", MakeColored(temps, -4f, CompareFunction.LessEqual)));
            setups.Add(new KeyValuePair<string, Material>("colored_bias-20", MakeColored(temps, -20f, CompareFunction.LessEqual)));
            setups.Add(new KeyValuePair<string, Material>("colored_always", MakeColored(temps, 0f, CompareFunction.Always)));

            // what is really on screen (an off-screen cam.Render() came back empty in VNyan)
            int w = Screen.width, h = Screen.height;
            float sx = w / (float)cam.pixelWidth, sy = h / (float)cam.pixelHeight;
            RectInt r = new RectInt(Mathf.Clamp((int)(area.xMin * sx), 0, w), Mathf.Clamp((int)(area.yMin * sy), 0, h),
                                    0, 0);
            r.width = Mathf.Clamp((int)(area.width * sx), 0, w - r.x);
            r.height = Mathf.Clamp((int)(area.height * sy), 0, h - r.y);
            int baseline = -1;
            foreach (var kv in setups)
            {
                if (kv.Value != null)
                {
                    var mats = new Material[Mathf.Max(1, testMesh.subMeshCount)];
                    for (int i = 0; i < mats.Length; i++) mats[i] = kv.Value;
                    t.sharedMaterials = mats;
                    t.enabled = true;
                }
                else t.enabled = false;
                for (int k = 0; k < testMesh.blendShapeCount; k++) t.SetBlendShapeWeight(k, b.GetBlendShapeWeight(k));
                yield return null;   // let the overlay skin this frame
                yield return null;
                yield return new WaitForEndOfFrame();
                Texture2D px = ScreenCapture.CaptureScreenshotAsTexture();
                temps.Add(px);
                if (px.width != w || px.height != h) { w = px.width; h = px.height; }
                int hits = 0;
                Color32[] all = px.GetPixels32();
                for (int y = r.y; y < r.y + r.height; y++)
                    for (int x = r.x; x < r.x + r.width; x++)
                    {
                        int idx = y * w + x;
                        if (idx < 0 || idx >= all.Length) continue;
                        Color32 c = all[idx];
                        if (c.r > 180 && c.g < 90 && c.b > 180) hits++;
                    }
                if (baseline < 0) baseline = hits;
                sb.AppendLine("render " + kv.Key + ": magenta pixels " + hits + " (baseline " + baseline + ", area " + (r.width * r.height) + ")");
                try { File.WriteAllBytes(Path.Combine(dir, kv.Key + ".png"), px.EncodeToPNG()); } catch { }
            }
            t.enabled = false;
            Finish(dir, sb, temps);
        }

        /// <summary>Drives the REAL heatmap exactly like the UI (select mesh, tick Heatmap) and captures the screen.</summary>
        IEnumerator ProbeHeatmap(string target)
        {
            string dir = Path.Combine(Application.persistentDataPath, "WeightStudio", "probe");
            var sb = new StringBuilder();
            var temps = new List<Object>();
            try { Directory.CreateDirectory(dir); } catch { }
            SkinnedMeshRenderer b = null;
            foreach (SkinnedMeshRenderer s in FindObjectsOfType<SkinnedMeshRenderer>())
                if (s != null && s.name == target) { b = s; break; }
            if (b == null) { sb.AppendLine("no renderer '" + target + "'"); Finish(dir, sb, temps); yield break; }
            EnsureCamera();
            Rect area = ScreenRect(b.bounds);
            yield return CaptureCount("heat_before", area, dir, sb, temps);

            SelectMesh(b);
            yield return null;
            SetHeatmap(true);
            for (int i = 0; i < 4; i++) yield return null;
            SkinnedMeshRenderer o = heatmap.OverlayRenderer;
            sb.AppendLine("after select+heatmap: base mesh=" + b.sharedMesh.name + "#" + b.sharedMesh.GetInstanceID() +
                          " overlay=" + (o != null ? o.name + " mesh=" + o.sharedMesh.name + "#" + o.sharedMesh.GetInstanceID() + " enabled=" + o.enabled +
                                         " active=" + o.gameObject.activeInHierarchy + " forceOff=" + o.forceRenderingOff + " visible=" + o.isVisible +
                                         " layer=" + o.gameObject.layer + " mat=" + (o.sharedMaterial != null ? o.sharedMaterial.shader.name + " q" + o.sharedMaterial.renderQueue + " col" + o.sharedMaterial.color : "null") : "NULL"));
            if (o != null)
            {
                var diff = new List<string>();
                for (int k = 0; k < b.sharedMesh.blendShapeCount; k++)
                {
                    float wb = b.GetBlendShapeWeight(k), wo = o.GetBlendShapeWeight(k);
                    if (Mathf.Abs(wb - wo) > 0.01f || Mathf.Abs(wb) > 0.01f) diff.Add(b.sharedMesh.GetBlendShapeName(k) + " base=" + wb.ToString("0.#") + " overlay=" + wo.ToString("0.#"));
                }
                sb.AppendLine("shape keys (non-zero or different): " + string.Join(", ", diff.ToArray()));
                Color32[] c = o.sharedMesh.colors32;
                sb.AppendLine("overlay mesh colours=" + (c != null ? c.Length : 0) + (c != null && c.Length > 0 ? " first=" + c[0] : ""));
                Mesh mb = new Mesh(), mo = new Mesh(); temps.Add(mb); temps.Add(mo);
                b.BakeMesh(mb, true); o.BakeMesh(mo, true);
                Vector3[] vb = mb.vertices, vo = mo.vertices;
                float max = 0f; for (int i = 0; i < Mathf.Min(vb.Length, vo.Length); i++) max = Mathf.Max(max, (vb[i] - vo[i]).magnitude);
                sb.AppendLine("baked base vs real overlay: max " + max.ToString("G4") + " (" + vb.Length + "/" + vo.Length + " verts)");
            }
            yield return CaptureCount("heat_on", area, dir, sb, temps);
            // brush surface (baked from Update) vs the shape actually rendered (end of frame)
            yield return null;
            RebuildPickSurface();
            Vector3[] pick = pickBake != null ? pickBake.vertices : new Vector3[0];
            yield return new WaitForEndOfFrame();
            Mesh real = new Mesh(); temps.Add(real);
            b.BakeMesh(real, true);
            Vector3[] rv = real.vertices;
            float pmax = 0f; for (int i = 0; i < Mathf.Min(pick.Length, rv.Length); i++) pmax = Mathf.Max(pmax, (pick[i] - rv[i]).magnitude);
            sb.AppendLine("brush surface vs rendered shape: max " + pmax.ToString("G4") + " m (" + pick.Length + "/" + rv.Length + " verts)");
            if (heatmap.OverlayRenderer != null)
                sb.AppendLine("overlay shape keys at end of frame: " + PhaseSample(heatmap.OverlayRenderer) + " | base " + PhaseSample(b));
            SetHeatmap(false);
            UnityEngine.UI.Toggle th = Find<UnityEngine.UI.Toggle>("Toggle_heatmap");
            if (th != null) th.SetIsOnWithoutNotify(false);
            Finish(dir, sb, temps);
        }

        // ---- open each of our plugins' windows like its button does, screenshot, close again
        static readonly string[] OurAssemblies = { "MaterialStudio", "VNyanWeightStudio", "VNyanWeightStudioReader", "VNyanBoneWeightSmoother",
            "PhysBones", "JigglePhysics", "PoseStudio", "SoftFleshField", "VRChatImporter", "WarudoImporter" };

        IEnumerator ProbeWindows()
        {
            string dir = Path.Combine(Application.persistentDataPath, "WeightStudio", "probe");
            var sb = new StringBuilder();
            var temps = new List<Object>();
            try { Directory.CreateDirectory(dir); } catch { }
            var handlers = new List<MonoBehaviour>();
            foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || !(mb is VNyanInterface.IButtonClickedHandler) || !mb.gameObject.scene.IsValid()) continue;
                string asm = mb.GetType().Assembly.GetName().Name;
                if (System.Array.IndexOf(OurAssemblies, asm) >= 0) handlers.Add(mb);
            }
            sb.AppendLine("plugin buttons found: " + handlers.Count);
            foreach (MonoBehaviour mb in handlers)
            {
                var h = (VNyanInterface.IButtonClickedHandler)mb;
                string tag = mb.GetType().Assembly.GetName().Name + "_" + mb.GetType().Name;
                bool opened = false;
                try { h.pluginButtonClicked(); opened = true; }
                catch (System.Exception e) { sb.AppendLine(tag + ": open failed: " + e.Message); }
                if (!opened) continue;
                for (int i = 0; i < 8; i++) yield return null;
                yield return new WaitForEndOfFrame();
                Texture2D px = ScreenCapture.CaptureScreenshotAsTexture();
                temps.Add(px);
                try { File.WriteAllBytes(Path.Combine(dir, "win_" + tag + ".png"), px.EncodeToPNG()); } catch { }
                try { h.pluginButtonClicked(); } catch (System.Exception e) { sb.AppendLine(tag + ": close failed: " + e.Message); }
                for (int i = 0; i < 4; i++) yield return null;
                sb.AppendLine("captured " + tag);
            }
            Finish(dir, sb, temps);
        }

        // ---- when in a frame does the renderer hold its real shape-key values?
        SkinnedMeshRenderer phaseR;
        StringBuilder phaseLog;
        int phaseFrames;

        string PhaseSample(SkinnedMeshRenderer r)
        {
            if (r == null || r.sharedMesh == null) return "-";
            int nz = 0; float sum = 0f; string first = "";
            for (int k = 0; k < r.sharedMesh.blendShapeCount; k++)
            {
                float w = r.GetBlendShapeWeight(k);
                if (Mathf.Abs(w) > 0.01f) { nz++; sum += w; if (first.Length == 0) first = r.sharedMesh.GetBlendShapeName(k) + "=" + w.ToString("0.#"); }
            }
            return nz + "/" + sum.ToString("0.#") + (first.Length > 0 ? " " + first : "");
        }

        void PhaseNote(string where)
        {
            if (phaseR == null || phaseLog == null || phaseFrames <= 0) return;
            phaseLog.AppendLine("  f" + Time.frameCount + " " + where + ": base " + PhaseSample(phaseR) +
                                (heatmap.OverlayRenderer != null ? " | overlay " + PhaseSample(heatmap.OverlayRenderer) : ""));
        }

        void OnPhasePreCull(Camera c) { if (c == cam) PhaseNote("onPreCull"); }
        void OnPhasePreRender(Camera c) { if (c == cam) PhaseNote("onPreRender"); }
        void OnPhasePostRender(Camera c) { if (c == cam) PhaseNote("onPostRender"); }

        IEnumerator ProbePhases(string target)
        {
            string dir = Path.Combine(Application.persistentDataPath, "WeightStudio", "probe");
            var sb = new StringBuilder();
            var temps = new List<Object>();
            try { Directory.CreateDirectory(dir); } catch { }
            EnsureCamera();
            SkinnedMeshRenderer b = null;
            foreach (SkinnedMeshRenderer s in FindObjectsOfType<SkinnedMeshRenderer>())
                if (s != null && s.name == target) { b = s; break; }
            if (b == null || cam == null) { sb.AppendLine("no renderer/camera"); Finish(dir, sb, temps); yield break; }
            phaseR = b;
            phaseLog = sb;
            Camera.onPreCull += OnPhasePreCull;
            Camera.onPreRender += OnPhasePreRender;
            Camera.onPostRender += OnPhasePostRender;
            for (int pass = 0; pass < 2; pass++)
            {
                sb.AppendLine(pass == 0 ? "--- before Weight Studio picks the mesh (mesh " + b.sharedMesh.name + ")" : "--- after SelectMesh + heatmap (mesh " + b.sharedMesh.name + ")");
                phaseFrames = 3;
                for (int f = 0; f < 3; f++)
                {
                    yield return null;
                    PhaseNote("after Update (coroutine)");
                    yield return new WaitForEndOfFrame();
                    PhaseNote("end of frame");
                }
                phaseFrames = 0;
                if (pass == 0) { SelectMesh(b); SetHeatmap(true); yield return null; yield return null; }
            }
            Camera.onPreCull -= OnPhasePreCull;
            Camera.onPreRender -= OnPhasePreRender;
            Camera.onPostRender -= OnPhasePostRender;
            phaseR = null; phaseLog = null;
            SetHeatmap(false);
            UnityEngine.UI.Toggle th = Find<UnityEngine.UI.Toggle>("Toggle_heatmap");
            if (th != null) th.SetIsOnWithoutNotify(false);
            Finish(dir, sb, temps);
        }

        IEnumerator CaptureCount(string tag, Rect area, string dir, StringBuilder sb, List<Object> temps)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D px = ScreenCapture.CaptureScreenshotAsTexture();
            temps.Add(px);
            int w = px.width, h = px.height, blue = 0, heat = 0;
            float sx = w / (float)Mathf.Max(1, cam.pixelWidth), sy = h / (float)Mathf.Max(1, cam.pixelHeight);
            Color32[] all = px.GetPixels32();
            int x0 = Mathf.Clamp((int)(area.xMin * sx), 0, w), x1 = Mathf.Clamp((int)(area.xMax * sx), 0, w);
            int y0 = Mathf.Clamp((int)(area.yMin * sy), 0, h), y1 = Mathf.Clamp((int)(area.yMax * sy), 0, h);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    Color32 c = all[y * w + x];
                    if (c.b > 200 && c.r < 70 && c.g < 70) blue++;
                    int mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    if (mx > 200 && mn < 60 && !(c.r > 200 && c.b > 200)) heat++;
                }
            sb.AppendLine("capture " + tag + ": blue " + blue + ", saturated " + heat + " in " + ((x1 - x0) * (y1 - y0)) + " px");
            try { File.WriteAllBytes(Path.Combine(dir, tag + ".png"), px.EncodeToPNG()); } catch { }
        }

        void DescribeRenderer(SkinnedMeshRenderer s, StringBuilder sb)
        {
            Mesh m = s.sharedMesh;
            sb.AppendLine("SMR '" + s.name + "' #" + s.GetInstanceID() + " path=" + PathTo(s.transform) + " enabled=" + s.enabled + " activeInHierarchy=" + s.gameObject.activeInHierarchy +
                          " forceRenderingOff=" + s.forceRenderingOff + " isVisible=" + s.isVisible + " layer=" + s.gameObject.layer +
                          " quality=" + s.quality + " offscreen=" + s.updateWhenOffscreen + " motionVec=" + s.skinnedMotionVectors +
                          " lossyScale=" + s.transform.lossyScale.ToString("F4"));
            sb.AppendLine("  mesh=" + (m != null ? m.name + "#" + m.GetInstanceID() + " v" + m.vertexCount + " bs" + m.blendShapeCount + " sub" + m.subMeshCount + " readable=" + m.isReadable + " colors=" + (m.colors32 != null ? m.colors32.Length : 0) : "NULL") +
                          " bones=" + s.bones.Length + " root=" + (s.rootBone != null ? s.rootBone.name : "null") +
                          " bounds=" + s.bounds.center.ToString("F3") + s.bounds.size.ToString("F3"));
            if (m != null)
            {
                var nz = new List<string>();
                for (int k = 0; k < m.blendShapeCount; k++)
                {
                    float wv = s.GetBlendShapeWeight(k);
                    if (Mathf.Abs(wv) > 0.001f) nz.Add(m.GetBlendShapeName(k) + "=" + wv.ToString("0.#"));
                }
                sb.AppendLine("  shapeKeys non-zero (" + nz.Count + "): " + string.Join(", ", nz.ToArray()));
            }
            foreach (Material mat in s.sharedMaterials)
            {
                if (mat == null) { sb.AppendLine("  mat NULL"); continue; }
                sb.AppendLine("  mat '" + mat.name + "' shader=" + (mat.shader != null ? mat.shader.name : "null") + " queue=" + mat.renderQueue +
                              " passes=" + mat.passCount + " keywords=[" + string.Join(" ", mat.shaderKeywords) + "]");
                if (mat.shader == null) continue;
                var depth = new List<string>();
                for (int i = 0; i < mat.shader.GetPropertyCount(); i++)
                {
                    var pt = mat.shader.GetPropertyType(i);
                    if (pt != ShaderPropertyType.Float && pt != ShaderPropertyType.Range) continue;
                    string pn = mat.shader.GetPropertyName(i);
                    string l = pn.ToLowerInvariant();
                    if (!(l.Contains("offset") || l.Contains("ztest") || l.Contains("zwrite") || l.Contains("cull") || l.Contains("vertex") || l.Contains("depth") || l.Contains("outline") || l.Contains("zbias"))) continue;
                    float v = mat.GetFloat(pn);
                    if (Mathf.Abs(v) > 1e-5f || l.Contains("ztest") || l.Contains("zwrite") || l.Contains("offsetfactor") || l.Contains("offsetunits")) depth.Add(pn + "=" + v.ToString("G4"));
                }
                sb.AppendLine("    depth/vertex props: " + string.Join(", ", depth.ToArray()));
            }
            foreach (Transform c in s.transform)
            {
                var comps = new List<string>();
                foreach (Component cc in c.GetComponents<Component>()) if (cc != null && !(cc is Transform)) comps.Add(cc.GetType().Name);
                Renderer cr = c.GetComponent<Renderer>();
                sb.AppendLine("  child '" + c.name + "' active=" + c.gameObject.activeInHierarchy + " [" + string.Join(",", comps.ToArray()) + "]" +
                              (cr != null ? " rEnabled=" + cr.enabled + " forceOff=" + cr.forceRenderingOff + " visible=" + cr.isVisible +
                                            " mats=" + cr.sharedMaterials.Length + " q=" + (cr.sharedMaterial != null ? cr.sharedMaterial.renderQueue.ToString() + " " + cr.sharedMaterial.shader.name : "-") : ""));
            }
        }

        static string PathTo(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 12; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        Rect ScreenRect(Bounds bb)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = bb.center + Vector3.Scale(bb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 s = cam.WorldToScreenPoint(c);
                if (s.z < 0) continue;
                x0 = Mathf.Min(x0, s.x); y0 = Mathf.Min(y0, s.y); x1 = Mathf.Max(x1, s.x); y1 = Mathf.Max(y1, s.y);
            }
            if (x0 > x1) return new Rect(0, 0, cam.pixelWidth, cam.pixelHeight);
            x0 = Mathf.Clamp(x0, 0, cam.pixelWidth); x1 = Mathf.Clamp(x1, 0, cam.pixelWidth);
            y0 = Mathf.Clamp(y0, 0, cam.pixelHeight); y1 = Mathf.Clamp(y1, 0, cam.pixelHeight);
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        static Material MakeSprites(List<Object> temps)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) return null;
            var m = new Material(sh) { name = "WS_ProbeSprites", color = Color.white };
            temps.Add(m);
            return m;
        }

        static Material MakeColored(List<Object> temps, float bias, CompareFunction zt)
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return null;
            var m = new Material(sh) { name = "WS_ProbeColored", color = Color.white };
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)zt);
            m.SetInt("_Cull", (int)CullMode.Off);
            m.SetFloat("_ZBias", bias);
            m.renderQueue = 3000;
            temps.Add(m);
            return m;
        }

        void Finish(string dir, StringBuilder sb, List<Object> temps)
        {
            foreach (Object o in temps) if (o != null) Object.Destroy(o);
            try { File.WriteAllText(Path.Combine(dir, "report.txt"), sb.ToString()); } catch { }
            Debug.Log("[WeightStudio][probe] done:\n" + sb);
            probeRunning = false;
        }
    }
}
