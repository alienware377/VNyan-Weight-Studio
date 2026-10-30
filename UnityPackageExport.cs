using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Writes a real <c>.unitypackage</c> (gzip-compressed USTAR tar, the same
    /// container the Unity editor produces) containing self-contained,
    /// Unity-2019.4 + 2022.3-compatible runtime scripts:
    ///   • <c>WeightStudioCollider</c>: sphere/capsule/plane collider component.
    ///   • <c>WeightStudioSpringBone</c>: lightweight spring-bone chain solver that
    ///     collides against those colliders (a plain-Unity port of the VNyan
    ///     PhysBones / Jiggle plugins).
    ///   • <c>WeightStudioColliderImporter</c>: rebuilds colliders on an avatar from
    ///     an exported <c>*.physbones.json</c> sidecar at runtime/edit time.
    /// If colliders are supplied, a ready-to-use sample JSON is bundled too.
    ///
    /// The scripts are embedded as text here so the installed plugin DLL is fully
    /// self-contained (no loose files to ship). Everything uses only UnityEngine and
    /// JsonUtility, so it compiles unchanged on both supported Unity versions.
    /// </summary>
    public static class UnityPackageExport
    {
        sealed class Asset
        {
            public string path;       // project path, e.g. "Assets/WeightStudio/Runtime/X.cs"
            public byte[] bytes;       // inline content (null for folders / file-backed assets)
            public string filePath;    // stream content from disk (for the big .vbx model)
            public long fileSize;      // size of filePath content
            public bool isFolder;
            public bool isScript;      // use MonoImporter meta
            public string importerGuid; // non-null => ScriptedImporter meta pointing at this script's guid
        }

        // Editor importer lives here; its guid (derived from the path) is what the
        // embedded .vbx asset's meta references so Unity imports it on drop-in.
        const string ImporterPath = "Assets/WeightStudio/Editor/VbxScriptedImporter.cs";

        /// <param name="vbxSourcePath">If non-null, this .vbx file is embedded as the
        /// importable model asset (streamed from disk so a multi-hundred-MB model
        /// doesn't have to sit in memory).</param>
        public static string Export(string path, IList<ColliderGen.GenCollider> colliders = null,
                                    string vbxSourcePath = null, string vbxAssetName = null)
        {
            var assets = new List<Asset>();
            Folder(assets, "Assets/WeightStudio");
            Folder(assets, "Assets/WeightStudio/Runtime");
            Script(assets, "Assets/WeightStudio/Runtime/WeightStudioCollider.cs", SCRIPT_COLLIDER);
            Script(assets, "Assets/WeightStudio/Runtime/WeightStudioSpringBone.cs", SCRIPT_SPRINGBONE);
            Script(assets, "Assets/WeightStudio/Runtime/WeightStudioColliderImporter.cs", SCRIPT_IMPORTER);
            Text(assets, "Assets/WeightStudio/Runtime/WeightStudio.Runtime.asmdef", ASMDEF);

            // Editor: the .vbx ScriptedImporter (rebuilds the avatar with unlimited weights)
            Folder(assets, "Assets/WeightStudio/Editor");
            Script(assets, ImporterPath, SCRIPT_VBX_IMPORTER);

            Text(assets, "Assets/WeightStudio/README.txt", README);

            if (colliders != null && colliders.Count > 0)
            {
                Folder(assets, "Assets/WeightStudio/Sample");
                string json = JsonConvert.SerializeObject(CollidersExport.JayoPhysBones(colliders), Formatting.Indented);
                Text(assets, "Assets/WeightStudio/Sample/generated_colliders.physbones.json", json);
            }

            // embed the model itself (imported by the ScriptedImporter on drop-in)
            if (!string.IsNullOrEmpty(vbxSourcePath) && File.Exists(vbxSourcePath))
            {
                Folder(assets, "Assets/WeightStudio/Model");
                string name = string.IsNullOrEmpty(vbxAssetName) ? "model.vbx" : vbxAssetName;
                if (!name.EndsWith(".vbx", StringComparison.OrdinalIgnoreCase)) name += ".vbx";
                assets.Add(new Asset
                {
                    path = "Assets/WeightStudio/Model/" + name,
                    filePath = vbxSourcePath,
                    fileSize = new FileInfo(vbxSourcePath).Length,
                    importerGuid = Guid32(ImporterPath)
                });
            }

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
                WriteArchive(gz, assets);
            return path;
        }

        // ---- asset helpers ----
        static void Folder(List<Asset> a, string p) { a.Add(new Asset { path = p, isFolder = true }); }
        static void Script(List<Asset> a, string p, string text) { a.Add(new Asset { path = p, bytes = Encoding.UTF8.GetBytes(text), isScript = true }); }
        static void Text(List<Asset> a, string p, string text) { a.Add(new Asset { path = p, bytes = Encoding.UTF8.GetBytes(text) }); }

        static string Guid32(string path)
        {
            using (var md5 = MD5.Create())
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(path))).ToString("N");
        }

        static string MetaFor(Asset a, string guid)
        {
            if (a.isFolder)
                return "fileFormatVersion: 2\nguid: " + guid + "\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
            if (a.isScript)
                return "fileFormatVersion: 2\nguid: " + guid + "\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
            if (!string.IsNullOrEmpty(a.importerGuid))
                // bind the .vbx to our ScriptedImporter so Unity rebuilds the avatar on import
                return "fileFormatVersion: 2\nguid: " + guid + "\nScriptedImporter:\n  internalIDToNameTable: []\n  externalObjects: {}\n  serializedVersion: 2\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n  script: {fileID: 11500000, guid: " + a.importerGuid + ", type: 3}\n  buildHumanoid: 1\n  forceUnlimitedSkinWeights: 1\n";
            return "fileFormatVersion: 2\nguid: " + guid + "\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
        }

        // ---- tar (USTAR), streamed straight into the gzip output ----
        static void WriteArchive(Stream s, List<Asset> assets)
        {
            foreach (var a in assets)
            {
                string guid = Guid32(a.path);
                byte[] meta = Encoding.UTF8.GetBytes(MetaFor(a, guid));
                byte[] pathname = Encoding.UTF8.GetBytes(a.path);

                if (!a.isFolder)
                {
                    if (a.filePath != null) TarEntryFromFile(s, guid + "/asset", a.filePath, a.fileSize);
                    else TarEntry(s, guid + "/asset", a.bytes);
                }
                TarEntry(s, guid + "/asset.meta", meta);
                TarEntry(s, guid + "/pathname", pathname);
            }
            // two zero blocks terminate the archive
            s.Write(new byte[1024], 0, 1024);
        }

        static void WriteTarHeader(Stream s, string name, long size)
        {
            var h = new byte[512];
            PutStr(h, 0, name, 100);
            PutOctal(h, 100, 0x1A4, 8);          // mode 0644
            PutOctal(h, 108, 0, 8);              // uid
            PutOctal(h, 116, 0, 8);              // gid
            PutOctal(h, 124, size, 12);          // size
            PutOctal(h, 136, 0, 12);             // mtime
            for (int i = 0; i < 8; i++) h[148 + i] = (byte)' '; // checksum placeholder
            h[156] = (byte)'0';                  // typeflag: regular file
            PutStr(h, 257, "ustar", 6);          // magic "ustar\0"
            h[263] = (byte)'0'; h[264] = (byte)'0'; // version "00"

            int sum = 0;
            for (int i = 0; i < 512; i++) sum += h[i];
            string cs = Convert.ToString(sum, 8).PadLeft(6, '0');
            for (int i = 0; i < 6; i++) h[148 + i] = (byte)cs[i];
            h[154] = 0; h[155] = (byte)' ';

            s.Write(h, 0, 512);
        }

        static void TarEntry(Stream s, string name, byte[] data)
        {
            int size = data != null ? data.Length : 0;
            WriteTarHeader(s, name, size);
            if (size > 0)
            {
                s.Write(data, 0, size);
                int pad = (512 - (size % 512)) % 512;
                if (pad > 0) s.Write(new byte[pad], 0, pad);
            }
        }

        static void TarEntryFromFile(Stream s, string name, string filePath, long size)
        {
            WriteTarHeader(s, name, size);
            using (var f = File.OpenRead(filePath))
            {
                var buf = new byte[1 << 20];
                long remaining = size; int r;
                while (remaining > 0 && (r = f.Read(buf, 0, (int)Math.Min(buf.Length, remaining))) > 0)
                {
                    s.Write(buf, 0, r);
                    remaining -= r;
                }
            }
            int pad = (int)((512 - (size % 512)) % 512);
            if (pad > 0) s.Write(new byte[pad], 0, pad);
        }

        static void PutStr(byte[] buf, int off, string str, int max)
        {
            byte[] b = Encoding.ASCII.GetBytes(str);
            int n = Math.Min(b.Length, max - 1);
            for (int i = 0; i < n; i++) buf[off + i] = b[i];
        }
        static void PutOctal(byte[] buf, int off, long val, int len)
        {
            string o = Convert.ToString(val, 8).PadLeft(len - 1, '0');
            for (int i = 0; i < len - 1; i++) buf[off + i] = (byte)o[i];
            buf[off + len - 1] = 0;
        }

        // =====================================================================
        //  Embedded Unity-compatible runtime scripts (2019.4.x + 2022.3.x)
        // =====================================================================

        const string ASMDEF =
"{\n    \"name\": \"WeightStudio.Runtime\",\n    \"references\": [],\n    \"includePlatforms\": [],\n    \"excludePlatforms\": [],\n    \"autoReferenced\": true\n}\n";

        const string README =
@"Weight Studio: Unity addon + model importer
===========================================

Drop this folder into any Unity 2019.4.x or 2022.3.x project. It contains:

  • Editor/VbxScriptedImporter.cs: a ScriptedImporter that turns a Weight Studio
    '.vbx' file into a full avatar prefab on import, KEEPING an unlimited number of
    bone influences per vertex (Mesh.SetBoneWeights). FBX import caps skinning at 4
    bones/vertex unless you choose Skin Weights = Custom; a .vbx has no such cap, and
    the importer also flips the project to QualitySettings.skinWeights = Unlimited so
    >4 influences actually render. Skeleton, blendshapes and best-effort materials/
    textures are rebuilt as sub-assets, with a Humanoid Avatar when the bone map allows.
  • Model/<avatar>.vbx: your exported model (present only if you had an avatar loaded
    at export time). Select it to see import options (Build Humanoid / Force Unlimited
    Skin Weights), or drag the imported prefab into a scene.
  • Plain-UnityEngine ports of the VNyan PhysBones / Jiggle physics, plus a loader for
    the colliders that Weight Studio auto-fits to your model.

Importing your own .vbx
-----------------------
Copy any '.vbx' that Weight Studio exported into this project (anywhere under Assets/).
Unity imports it with VbxScriptedImporter automatically. If a freshly added file shows
as a generic asset, right-click it > Reimport. To render >4 influences manually, set
Edit > Project Settings > Quality > Other > Skin Weights to 'Unlimited' (the importer
does this for you).

Components
----------
WeightStudioCollider      Sphere / Capsule / Plane collider. Sits on a bone; its
                          centre/radius are in the bone's local space (exactly what
                          Weight Studio generated). Draws a gizmo when selected.

WeightStudioSpringBone    A lightweight spring-bone chain solver (hair, tails,
                          accessories). Set 'root' to the first bone of the chain
                          and drop colliders into its list to get collisions.

WeightStudioColliderImporter
                          Reads an exported '<avatar>.physbones.json' (assign it as
                          a TextAsset, or give a file path) and recreates the
                          colliders on the matching bones by name. Add it to the
                          avatar root and press Build (or let it run on Awake).

The same JSON also loads directly into the VNyan PhysBones and Jiggle plugins, and
the colliders are embedded inside exported .vbx / .vrm files.
";

        const string SCRIPT_COLLIDER =
@"using UnityEngine;

namespace WeightStudio
{
    public enum WSColliderShape { Sphere, Capsule, Plane }

    /// <summary>A bone-local collider that pushes points out of its volume.</summary>
    [AddComponentMenu(""Weight Studio/WS Collider"")]
    public class WeightStudioCollider : MonoBehaviour
    {
        public WSColliderShape shape = WSColliderShape.Sphere;
        public Vector3 center;              // local centre / capsule end A / plane point
        public Vector3 centerEnd;           // local capsule end B
        public Vector3 axis = Vector3.up;   // capsule axis / plane normal (local)
        public float radius = 0.1f;
        public Color gizmoColor = new Color(0.2f, 0.8f, 1f, 0.6f);

        Vector3 W(Vector3 l) { return transform.TransformPoint(l); }
        Vector3 WDir(Vector3 l) { Vector3 d = transform.TransformDirection(l); return d.sqrMagnitude > 1e-12f ? d.normalized : Vector3.up; }

        /// <summary>Pushes world point p out of the collider; returns penetration depth.</summary>
        public float Resolve(ref Vector3 p, float pointRadius)
        {
            switch (shape)
            {
                case WSColliderShape.Sphere:
                {
                    float r = radius + pointRadius;
                    Vector3 c = W(center);
                    Vector3 d = p - c; float m = d.magnitude;
                    if (m < r) { p = m > 1e-6f ? c + d * (r / m) : c + Vector3.up * r; return r - m; }
                    return 0f;
                }
                case WSColliderShape.Capsule:
                {
                    float r = radius + pointRadius;
                    Vector3 a = W(center), b = W(centerEnd);
                    Vector3 cp = ClosestOnSegment(a, b, p);
                    Vector3 d = p - cp; float m = d.magnitude;
                    if (m < r) { p = m > 1e-6f ? cp + d * (r / m) : cp + Vector3.up * r; return r - m; }
                    return 0f;
                }
                case WSColliderShape.Plane:
                {
                    Vector3 c = W(center); Vector3 n = WDir(axis);
                    float dist = Vector3.Dot(p - c, n);
                    if (dist < pointRadius) { float pen = pointRadius - dist; p += n * pen; return pen; }
                    return 0f;
                }
            }
            return 0f;
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a; float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return a + ab * t;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = gizmoColor;
            if (shape == WSColliderShape.Plane)
            {
                Vector3 c = W(center); Vector3 n = WDir(axis);
                Gizmos.DrawLine(c, c + n * 0.1f);
                return;
            }
            Gizmos.DrawWireSphere(W(center), radius);
            if (shape == WSColliderShape.Capsule)
            {
                Gizmos.DrawWireSphere(W(centerEnd), radius);
                Gizmos.DrawLine(W(center), W(centerEnd));
            }
        }
    }
}
";

        const string SCRIPT_SPRINGBONE =
@"using System.Collections.Generic;
using UnityEngine;

namespace WeightStudio
{
    /// <summary>
    /// Lightweight spring-bone chain solver (hair / tail / accessories) that collides
    /// against WeightStudioCollider components. A plain-Unity port of the VNyan
    /// PhysBones / Jiggle runtime. Set 'root' to the first bone of the chain.
    /// </summary>
    [AddComponentMenu(""Weight Studio/WS Spring Bone"")]
    public class WeightStudioSpringBone : MonoBehaviour
    {
        public Transform root;
        [Range(0f, 1f)] public float stiffness = 0.2f;
        [Range(0f, 1f)] public float damping = 0.2f;
        [Range(0f, 1f)] public float pull = 0.2f;
        public float boneRadius = 0.02f;
        public Vector3 gravity = new Vector3(0f, -1f, 0f);
        public float gravityScale = 0f;
        public List<WeightStudioCollider> colliders = new List<WeightStudioCollider>();

        class P
        {
            public Transform t; public P parent; public Vector3 pos; public Vector3 prev;
            public Vector3 restLocalPos; public float restLen; public bool isRoot;
            public List<P> kids = new List<P>();
        }
        readonly List<P> parts = new List<P>();

        void Start() { Rebuild(); }

        public void Rebuild()
        {
            parts.Clear();
            if (root != null) Build(root, null);
        }

        void Build(Transform t, P parent)
        {
            var p = new P { t = t, parent = parent, pos = t.position, prev = t.position, isRoot = parent == null };
            if (parent != null)
            {
                p.restLocalPos = t.localPosition;
                p.restLen = (t.position - parent.t.position).magnitude;
                parent.kids.Add(p);
            }
            parts.Add(p);
            for (int i = 0; i < t.childCount; i++) Build(t.GetChild(i), p);
        }

        void LateUpdate()
        {
            int n = parts.Count;
            if (n == 0) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // integrate (parent processed before child thanks to build order)
            for (int i = 0; i < n; i++)
            {
                P p = parts[i];
                if (p.isRoot) { p.prev = p.pos; p.pos = p.t.position; continue; }
                Vector3 rest = p.parent.t.TransformPoint(p.restLocalPos);
                Vector3 inert = (p.pos - p.prev) * (1f - damping);
                Vector3 next = p.pos + inert + gravity * gravityScale * dt;
                next = Vector3.Lerp(next, rest, stiffness);
                p.prev = p.pos;
                p.pos = next;
            }

            // length constraint + pull-to-rest + collisions
            for (int i = 0; i < n; i++)
            {
                P p = parts[i];
                if (p.isRoot) continue;
                Vector3 rest = p.parent.t.TransformPoint(p.restLocalPos);
                Vector3 dir = p.pos - p.parent.pos;
                float len = dir.magnitude;
                float target = p.restLen > 1e-6f ? p.restLen : len;
                if (len > 1e-6f) p.pos = p.parent.pos + dir * (target / len);
                p.pos = Vector3.Lerp(p.pos, rest, pull * pull);
                for (int c = 0; c < colliders.Count; c++)
                    if (colliders[c] != null) colliders[c].Resolve(ref p.pos, boneRadius);
            }

            // write rotations: aim each parent toward its children
            for (int i = 0; i < n; i++)
            {
                P p = parts[i];
                if (p.kids.Count == 0) continue;
                Vector3 restDir = Vector3.zero, curDir = Vector3.zero;
                for (int k = 0; k < p.kids.Count; k++)
                {
                    restDir += (p.kids[k].t.position - p.t.position);
                    curDir += (p.kids[k].pos - p.pos);
                }
                if (restDir.sqrMagnitude < 1e-10f || curDir.sqrMagnitude < 1e-10f) continue;
                p.t.rotation = Quaternion.FromToRotation(restDir.normalized, curDir.normalized) * p.t.rotation;
            }
        }
    }
}
";

        const string SCRIPT_IMPORTER =
@"using System.Collections.Generic;
using UnityEngine;

namespace WeightStudio
{
    /// <summary>
    /// Recreates WeightStudioCollider components on an avatar from a Weight Studio
    /// '*.physbones.json' / '*.jigglephysics.json' sidecar (bones matched by name).
    /// Assign the JSON as a TextAsset, or set an absolute file path.
    /// </summary>
    [AddComponentMenu(""Weight Studio/WS Collider Importer"")]
    public class WeightStudioColliderImporter : MonoBehaviour
    {
        public Transform avatarRoot;
        public TextAsset configJson;
        public string configPath;
        public bool buildOnAwake = true;

        [System.Serializable]
        public class Entry
        {
            public string name; public string type; public string bone;
            public float[] offset; public float[] offsetEnd; public float[] axis;
            public float radius; public float height;
        }
        [System.Serializable]
        public class Doc { public List<Entry> colliders = new List<Entry>(); }

        void Awake() { if (buildOnAwake) Build(); }

        public int Build()
        {
            string json = configJson != null ? configJson.text
                : (!string.IsNullOrEmpty(configPath) && System.IO.File.Exists(configPath) ? System.IO.File.ReadAllText(configPath) : null);
            if (string.IsNullOrEmpty(json)) { Debug.LogWarning(""[WeightStudio] No collider config assigned.""); return 0; }

            Doc doc = JsonUtility.FromJson<Doc>(json);
            if (doc == null || doc.colliders == null) return 0;

            Transform rootT = avatarRoot != null ? avatarRoot : transform;
            var byName = new Dictionary<string, Transform>();
            foreach (var t in rootT.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName[t.name] = t;

            int made = 0;
            foreach (var e in doc.colliders)
            {
                Transform bone;
                if (string.IsNullOrEmpty(e.bone) || !byName.TryGetValue(e.bone, out bone)) continue;
                var col = bone.gameObject.AddComponent<WeightStudioCollider>();
                col.shape = e.type == ""capsule"" ? WSColliderShape.Capsule
                          : (e.type == ""plane"" ? WSColliderShape.Plane : WSColliderShape.Sphere);
                col.center = V(e.offset);
                col.centerEnd = (e.offsetEnd != null && e.offsetEnd.Length >= 3) ? V(e.offsetEnd) : col.center;
                col.axis = (e.axis != null && e.axis.Length >= 3) ? V(e.axis) : Vector3.up;
                col.radius = e.radius;
                made++;
            }
            Debug.Log(""[WeightStudio] Built "" + made + "" colliders from config."");
            return made;
        }

        static Vector3 V(float[] a) { return (a != null && a.Length >= 3) ? new Vector3(a[0], a[1], a[2]) : Vector3.zero; }
    }
}
";

        // =====================================================================
        //  Editor ScriptedImporter for .vbx  (Unity 2019.4.x + 2022.3.x)
        //  Rebuilds the avatar with UNLIMITED bone influences. Self-contained:
        //  inlines a VBX reader so it needs no other Weight Studio script.
        // =====================================================================
        const string SCRIPT_VBX_IMPORTER =
@"using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEditor;
#if UNITY_2020_2_OR_NEWER
using UnityEditor.AssetImporters;
#else
using UnityEditor.Experimental.AssetImporters;
#endif

namespace WeightStudio
{
    /// <summary>
    /// Imports a Weight Studio '.vbx' avatar as a prefab asset: skeleton, skinned
    /// meshes with an UNLIMITED number of bone influences per vertex, blendshapes,
    /// and best-effort materials/textures. Unlike FBX (capped at 4 bones/vertex
    /// unless Skin Weights = Custom), a .vbx keeps every influence.
    /// </summary>
    [ScriptedImporter(3, ""vbx"")]
    public class VbxScriptedImporter : ScriptedImporter
    {
        [Tooltip(""Build a Humanoid Avatar from the embedded human bone map, if present."")]
        public bool buildHumanoid = true;
        [Tooltip(""Set the project to Unlimited skin weights so >4 influences render."")]
        public bool forceUnlimitedSkinWeights = true;

        Doc _doc;                                  // current document (for shared texture pool)
        Dictionary<string, Texture2D> _texCache;   // decoded pooled textures, key ""index:linear""

        public override void OnImportAsset(AssetImportContext ctx)
        {
            Doc doc;
            using (var fs = File.OpenRead(ctx.assetPath)) doc = Read(fs);
            _doc = doc;
            _texCache = new Dictionary<string, Texture2D>();

            if (forceUnlimitedSkinWeights && QualitySettings.skinWeights != SkinWeights.Unlimited)
                QualitySettings.skinWeights = SkinWeights.Unlimited;

            var root = new GameObject(string.IsNullOrEmpty(doc.avatarName) ? ""VbxAvatar"" : doc.avatarName);

            var boneT = new Transform[doc.bones.Count];
            for (int i = 0; i < doc.bones.Count; i++)
                boneT[i] = (i == 0) ? root.transform : new GameObject(doc.bones[i].name).transform;
            for (int i = 0; i < doc.bones.Count; i++)
            {
                var b = doc.bones[i];
                Transform t = boneT[i];
                if (i != 0)
                {
                    Transform parent = (b.parent >= 0 && b.parent < boneT.Length) ? boneT[b.parent] : root.transform;
                    t.SetParent(parent, false);
                }
                t.localPosition = b.localPos;
                t.localRotation = b.localRot;
                t.localScale = b.localScale;
            }

            int n = 0;
            foreach (var vm in doc.meshes) { BuildMesh(ctx, vm, root.transform, boneT, n); n++; }

            if (buildHumanoid) TryBuildHumanoid(ctx, root, doc, boneT);

            ctx.AddObjectToAsset(""root"", root);
            ctx.SetMainObject(root);
        }

        void BuildMesh(AssetImportContext ctx, MeshD vm, Transform root, Transform[] boneT, int meshNo)
        {
            var go = new GameObject(string.IsNullOrEmpty(vm.name) ? ""Mesh"" : vm.name);
            go.transform.SetParent(root, false);
            var smr = go.AddComponent<SkinnedMeshRenderer>();

            var mesh = new Mesh();
            mesh.name = string.IsNullOrEmpty(vm.name) ? (""Mesh"" + meshNo) : vm.name;
            mesh.indexFormat = (vm.positions != null && vm.positions.Length > 65535)
                ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = vm.positions ?? new Vector3[0];
            if (vm.normals != null && vm.normals.Length == mesh.vertexCount) mesh.normals = vm.normals;
            if (vm.tangents != null && vm.tangents.Length == mesh.vertexCount) mesh.tangents = vm.tangents;
            if (vm.uv != null && vm.uv.Length == mesh.vertexCount) mesh.uv = vm.uv;
            if (vm.uv2 != null && vm.uv2.Length == mesh.vertexCount) mesh.uv2 = vm.uv2;
            if (vm.bindposes != null && vm.bindposes.Length > 0) mesh.bindposes = vm.bindposes;

            int subCount = vm.submeshes != null ? vm.submeshes.Length : 0;
            mesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++) mesh.SetTriangles(vm.submeshes[s] ?? new int[0], s);

            ApplyWeights(mesh, vm);

            if (vm.blendShapes != null)
                foreach (var bs in vm.blendShapes)
                    foreach (var fr in bs.frames)
                        mesh.AddBlendShapeFrame(bs.name, fr.weight, fr.dV, fr.dN, fr.dT);

            if (mesh.normals == null || mesh.normals.Length == 0) mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var bones = new Transform[vm.boneMap != null ? vm.boneMap.Length : 0];
            for (int i = 0; i < bones.Length; i++)
            {
                int gi = vm.boneMap[i];
                bones[i] = (gi >= 0 && gi < boneT.Length) ? boneT[gi] : null;
            }

            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = (vm.rootBone >= 0 && vm.rootBone < boneT.Length) ? boneT[vm.rootBone]
                          : (bones.Length > 0 ? bones[0] : root);
            smr.quality = SkinQuality.Auto;
            smr.updateWhenOffscreen = true;
            smr.localBounds = mesh.bounds;

            ctx.AddObjectToAsset(""mesh"" + meshNo, mesh);

            var mats = new Material[Mathf.Max(subCount, 1)];
            for (int i = 0; i < mats.Length; i++)
            {
                Mat vmat = (vm.materials != null && i < vm.materials.Count) ? vm.materials[i] : null;
                mats[i] = BuildMaterial(ctx, vmat, meshNo, i);
            }
            smr.sharedMaterials = mats;
        }

        void ApplyWeights(Mesh mesh, MeshD vm)
        {
            int vc = mesh.vertexCount;
            if (vm.bonesPerVertex == null || vm.bonesPerVertex.Length != vc ||
                vm.influenceBone == null || vm.influenceWeight == null) return;
            int inf = vm.influenceBone.Length;
            var bpv = new NativeArray<byte>(vm.bonesPerVertex, Allocator.Temp);
            var weights = new NativeArray<BoneWeight1>(inf, Allocator.Temp);
            for (int i = 0; i < inf; i++)
                weights[i] = new BoneWeight1 { boneIndex = vm.influenceBone[i], weight = vm.influenceWeight[i] };
            try { mesh.SetBoneWeights(bpv, weights); }
            finally { bpv.Dispose(); weights.Dispose(); }
        }

        Material BuildMaterial(AssetImportContext ctx, Mat vmat, int meshNo, int idx)
        {
            Shader shader = null;
            if (vmat != null && !string.IsNullOrEmpty(vmat.shader)) shader = Shader.Find(vmat.shader);
            if (shader == null) shader = Shader.Find(""Standard"");
            if (shader == null) shader = Shader.Find(""Universal Render Pipeline/Lit"");
            if (shader == null) shader = Shader.Find(""Sprites/Default"");
            var mat = new Material(shader);
            mat.name = (vmat != null && !string.IsNullOrEmpty(vmat.name)) ? vmat.name : (""Material"" + meshNo + ""_"" + idx);
            if (vmat != null)
            {
                bool haveGeneric = (vmat.colors != null && vmat.colors.Count > 0) ||
                                   (vmat.floats != null && vmat.floats.Count > 0) ||
                                   (vmat.vectors != null && vmat.vectors.Count > 0) ||
                                   (vmat.texProps != null && vmat.texProps.Count > 0);
                if (haveGeneric)
                {
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
                        foreach (var tp in vmat.texProps)
                        {
                            if (!mat.HasProperty(tp.name)) continue;
                            if (tp.texIndex >= 0)
                            {
                                bool lin = IsLinearProp(tp.name);
                                Texture2D tex = DecodePooled(ctx, tp.texIndex, lin, meshNo, idx);
                                if (tex != null) mat.SetTexture(tp.name, tex);
                            }
                            mat.SetTextureScale(tp.name, tp.scale);
                            mat.SetTextureOffset(tp.name, tp.offset);
                        }
                    if (vmat.renderQueue >= 0) mat.renderQueue = vmat.renderQueue;
                }
                else
                {
                    // legacy (v1/v2) fallback
                    SetColor(mat, ""_Color"", vmat.color); SetColor(mat, ""_BaseColor"", vmat.color);
                    if (vmat.emission != Color.black) { SetColor(mat, ""_EmissionColor"", vmat.emission); mat.EnableKeyword(""_EMISSION""); }
                    if (mat.HasProperty(""_Cutoff"")) mat.SetFloat(""_Cutoff"", vmat.cutoff);
                    if (vmat.renderQueue >= 0) mat.renderQueue = vmat.renderQueue;
                    Texture2D main = Png(ctx, vmat.mainTexPng, false, meshNo, idx, ""main"");
                    if (main != null) { SetTex(mat, ""_MainTex"", main); SetTex(mat, ""_BaseMap"", main); }
                    Texture2D nrm = Png(ctx, vmat.normalTexPng, true, meshNo, idx, ""nrm"");
                    if (nrm != null) { SetTex(mat, ""_BumpMap"", nrm); SetTex(mat, ""_NormalMap"", nrm); mat.EnableKeyword(""_NORMALMAP""); }
                    Texture2D emis = Png(ctx, vmat.emissionTexPng, false, meshNo, idx, ""emis"");
                    if (emis != null) SetTex(mat, ""_EmissionMap"", emis);
                }
            }
            ctx.AddObjectToAsset(""mat"" + meshNo + ""_"" + idx, mat);
            return mat;
        }

        static bool IsLinearProp(string prop)
        {
            if (string.IsNullOrEmpty(prop)) return false;
            string p = prop.ToLowerInvariant();
            return p.Contains(""bump"") || p.Contains(""normal"");
        }

        Texture2D DecodePooled(AssetImportContext ctx, int index, bool linear, int meshNo, int idx)
        {
            if (_doc == null || _doc.textures == null || index < 0 || index >= _doc.textures.Count) return null;
            string key = index + "":"" + (linear ? ""1"" : ""0"");
            Texture2D tex;
            if (_texCache.TryGetValue(key, out tex)) return tex;
            byte[] png = _doc.textures[index];
            tex = Png(ctx, png, linear, meshNo, idx, ""pool"" + index + (linear ? ""L"" : """"));
            _texCache[key] = tex;
            return tex;
        }

        static void SetColor(Material m, string p, Color c) { if (m.HasProperty(p)) m.SetColor(p, c); }
        static void SetTex(Material m, string p, Texture t) { if (m.HasProperty(p)) m.SetTexture(p, t); }

        Texture2D Png(AssetImportContext ctx, byte[] png, bool linear, int meshNo, int idx, string kind)
        {
            if (png == null || png.Length == 0) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
            if (!tex.LoadImage(png)) { UnityEngine.Object.DestroyImmediate(tex); return null; }
            tex.name = ""tex"" + meshNo + ""_"" + idx + ""_"" + kind;
            tex.wrapMode = TextureWrapMode.Repeat;
            ctx.AddObjectToAsset(tex.name, tex);
            return tex;
        }

        void TryBuildHumanoid(AssetImportContext ctx, GameObject root, Doc doc, Transform[] boneT)
        {
            var animator = root.AddComponent<Animator>();
            if (doc.humanoid == null || doc.humanoid.Count == 0) return;
            try
            {
                string[] traits = HumanTrait.BoneName;
                var hbs = new List<HumanBone>();
                foreach (var hb in doc.humanoid)
                {
                    if (hb.humanBone < 0 || hb.humanBone >= traits.Length) continue;
                    if (hb.boneIndex < 0 || hb.boneIndex >= boneT.Length) continue;
                    Transform t = boneT[hb.boneIndex];
                    if (t == null) continue;
                    hbs.Add(new HumanBone { humanName = traits[hb.humanBone], boneName = t.name, limit = new HumanLimit { useDefaultValues = true } });
                }
                if (hbs.Count == 0) return;
                var skel = new List<SkeletonBone>();
                for (int i = 0; i < doc.bones.Count; i++)
                {
                    var b = doc.bones[i];
                    skel.Add(new SkeletonBone { name = boneT[i].name, position = b.localPos, rotation = b.localRot, scale = b.localScale });
                }
                var desc = new HumanDescription
                {
                    human = hbs.ToArray(), skeleton = skel.ToArray(),
                    upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                    armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false
                };
                Avatar avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
                if (avatar != null && avatar.isValid)
                {
                    avatar.name = root.name + ""_Avatar"";
                    animator.avatar = avatar;
                    ctx.AddObjectToAsset(""avatar"", avatar);
                }
                else Debug.LogWarning(""[WeightStudio] Humanoid avatar invalid; left as Generic."");
            }
            catch (Exception e) { Debug.LogWarning(""[WeightStudio] Humanoid rebuild failed: "" + e.Message); }
        }

        // ----------------------- inlined VBX reader (v1/v2/v3) -----------------------
        class Doc { public string avatarName = ""avatar""; public List<Bone> bones = new List<Bone>(); public List<HumanB> humanoid = new List<HumanB>(); public List<MeshD> meshes = new List<MeshD>(); public List<byte[]> textures = new List<byte[]>(); }
        class Bone { public string name; public int parent; public Vector3 localPos; public Quaternion localRot; public Vector3 localScale; }
        class HumanB { public int humanBone; public int boneIndex; }
        class MeshD { public string name; public int[] boneMap; public int rootBone; public Vector3[] positions; public Vector3[] normals; public Vector4[] tangents; public Vector2[] uv; public Vector2[] uv2; public Matrix4x4[] bindposes; public byte[] bonesPerVertex; public int[] influenceBone; public float[] influenceWeight; public int[][] submeshes; public List<BlendShape> blendShapes = new List<BlendShape>(); public List<Mat> materials = new List<Mat>(); }
        class BlendShape { public string name; public List<BlendFrame> frames = new List<BlendFrame>(); }
        class BlendFrame { public float weight; public Vector3[] dV; public Vector3[] dN; public Vector3[] dT; }
        class Mat { public string name; public string shader; public Color color = Color.white; public Color emission = Color.black; public float cutoff = 0.5f; public int renderQueue = -1; public byte[] mainTexPng; public byte[] normalTexPng; public byte[] emissionTexPng; public string[] keywords; public List<MatF> floats = new List<MatF>(); public List<MatC> colors = new List<MatC>(); public List<MatV> vectors = new List<MatV>(); public List<MatT> texProps = new List<MatT>(); }
        class MatF { public string name; public float value; }
        class MatC { public string name; public Color value; }
        class MatV { public string name; public Vector4 value; }
        class MatT { public string name; public int texIndex; public Vector2 scale = Vector2.one; public Vector2 offset = Vector2.zero; }

        const uint Magic = 0x31584256;

        static Doc Read(Stream stream)
        {
            var r = new BinaryReader(stream, Encoding.UTF8);
            uint magic = r.ReadUInt32();
            if (magic != Magic) throw new InvalidDataException(""Not a VBX file (bad magic)."");
            int ver = r.ReadInt32();
            if (ver < 1 || ver > 3) throw new InvalidDataException(""Unsupported VBX version "" + ver + ""."");
            var doc = new Doc();
            doc.avatarName = r.ReadString();
            int bc = r.ReadInt32();
            for (int i = 0; i < bc; i++)
                doc.bones.Add(new Bone { name = r.ReadString(), parent = r.ReadInt32(), localPos = RV3(r), localRot = RQ(r), localScale = RV3(r) });
            int hc = r.ReadInt32();
            for (int i = 0; i < hc; i++)
                doc.humanoid.Add(new HumanB { humanBone = r.ReadInt32(), boneIndex = r.ReadInt32() });
            int mc = r.ReadInt32();
            for (int i = 0; i < mc; i++)
            {
                var m = new MeshD();
                m.name = r.ReadString();
                m.boneMap = RIntArr(r);
                m.rootBone = r.ReadInt32();
                int vc = r.ReadInt32();
                m.positions = RV3Arr(r);
                m.normals = RV3Arr(r);
                m.tangents = RV4Arr(r);
                m.uv = RV2Arr(r);
                m.uv2 = RV2Arr(r);
                int bp = r.ReadInt32();
                m.bindposes = new Matrix4x4[bp];
                for (int k = 0; k < bp; k++) m.bindposes[k] = RM4(r);
                int bpvLen = r.ReadInt32();
                m.bonesPerVertex = bpvLen > 0 ? r.ReadBytes(bpvLen) : new byte[0];
                int inf = r.ReadInt32();
                m.influenceBone = new int[inf]; m.influenceWeight = new float[inf];
                for (int k = 0; k < inf; k++) { m.influenceBone[k] = r.ReadInt32(); m.influenceWeight[k] = r.ReadSingle(); }
                int sm = r.ReadInt32();
                m.submeshes = new int[sm][];
                for (int s = 0; s < sm; s++) m.submeshes[s] = RIntArr(r);
                int bsc = r.ReadInt32();
                for (int s = 0; s < bsc; s++)
                {
                    var bs = new BlendShape { name = r.ReadString() };
                    int fc = r.ReadInt32();
                    for (int f = 0; f < fc; f++)
                        bs.frames.Add(new BlendFrame { weight = r.ReadSingle(), dV = RV3Arr(r), dN = RV3Arr(r), dT = RV3Arr(r) });
                    m.blendShapes.Add(bs);
                }
                int matc = r.ReadInt32();
                for (int s = 0; s < matc; s++)
                {
                    var mt = new Mat();
                    mt.name = r.ReadString(); mt.shader = r.ReadString();
                    mt.color = RColor(r); mt.emission = RColor(r);
                    mt.cutoff = r.ReadSingle(); mt.renderQueue = r.ReadInt32();
                    mt.mainTexPng = RBlob(r); mt.normalTexPng = RBlob(r); mt.emissionTexPng = RBlob(r);
                    if (ver >= 3)
                    {
                        int kwn = r.ReadInt32();
                        mt.keywords = new string[kwn];
                        for (int k = 0; k < kwn; k++) mt.keywords[k] = r.ReadString();
                        int fn = r.ReadInt32();
                        for (int k = 0; k < fn; k++) mt.floats.Add(new MatF { name = r.ReadString(), value = r.ReadSingle() });
                        int cn = r.ReadInt32();
                        for (int k = 0; k < cn; k++) mt.colors.Add(new MatC { name = r.ReadString(), value = RColor(r) });
                        int vn = r.ReadInt32();
                        for (int k = 0; k < vn; k++) mt.vectors.Add(new MatV { name = r.ReadString(), value = RV4(r) });
                        int tn = r.ReadInt32();
                        for (int k = 0; k < tn; k++)
                            mt.texProps.Add(new MatT { name = r.ReadString(), texIndex = r.ReadInt32(), scale = RV2(r), offset = RV2(r) });
                    }
                    m.materials.Add(mt);
                }
                doc.meshes.Add(m);
            }

            // colliders (v2+): read past them so the texture pool aligns
            if (ver >= 2)
            {
                int cc = r.ReadInt32();
                for (int i = 0; i < cc; i++)
                {
                    r.ReadString(); r.ReadString(); r.ReadInt32(); r.ReadInt32();   // name, boneName, boneIndex, shape
                    RV3(r); RV3(r); RV3(r);                                          // center, centerEnd, axis
                    r.ReadSingle(); r.ReadSingle();                                  // radius, height
                }
            }

            // v3+ shared texture pool (trailing block)
            if (ver >= 3)
            {
                int tpn = r.ReadInt32();
                for (int i = 0; i < tpn; i++) doc.textures.Add(RBlob(r));
            }
            return doc;
        }

        static Vector2 RV2(BinaryReader r) { return new Vector2(r.ReadSingle(), r.ReadSingle()); }
        static Vector3 RV3(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static Vector4 RV4(BinaryReader r) { return new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static Quaternion RQ(BinaryReader r) { return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static Color RColor(BinaryReader r) { return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static Matrix4x4 RM4(BinaryReader r) { var m = new Matrix4x4(); for (int i = 0; i < 16; i++) m[i] = r.ReadSingle(); return m; }
        static int[] RIntArr(BinaryReader r) { int n = r.ReadInt32(); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = r.ReadInt32(); return a; }
        static Vector2[] RV2Arr(BinaryReader r) { int n = r.ReadInt32(); var a = new Vector2[n]; for (int i = 0; i < n; i++) a[i] = RV2(r); return a; }
        static Vector3[] RV3Arr(BinaryReader r) { int n = r.ReadInt32(); var a = new Vector3[n]; for (int i = 0; i < n; i++) a[i] = RV3(r); return a; }
        static Vector4[] RV4Arr(BinaryReader r) { int n = r.ReadInt32(); var a = new Vector4[n]; for (int i = 0; i < n; i++) a[i] = RV4(r); return a; }
        static byte[] RBlob(BinaryReader r) { int n = r.ReadInt32(); return n > 0 ? r.ReadBytes(n) : null; }
    }
}
";
    }
}
