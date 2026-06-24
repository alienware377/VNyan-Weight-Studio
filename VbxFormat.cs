using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace VNyanWeightStudio
{
    // ============================================================================
    //  VBX  —  "VNyan Bone eXchange"
    //
    //  A self-contained binary avatar-skinning interchange format. Unlike VRM/glTF
    //  (which the VRM consumer convention caps at 4 bone influences per vertex), VBX
    //  stores an UNLIMITED number of bone influences per vertex, plus the full
    //  skeleton, bind poses, blendshapes, and best-effort materials/textures.
    //
    //  The Weight Studio plugin WRITES .vbx; the companion Reader plugin READS it
    //  and reconstructs the avatar at VNyan runtime with all influences intact.
    //
    //  This file (the data classes + serializer) is shared verbatim between both
    //  plugins. Keep the two copies identical.
    // ============================================================================

    public sealed class VbxDoc
    {
        public const uint Magic = 0x31584256;   // "VBX1" little-endian
        // Version history:
        //   1 — bones, humanoid, meshes (geometry/weights/blendshapes/materials).
        //   2 — adds the colliders block (auto-fit physics colliders) at the end.
        //   3 — full generic materials: per material we now also store the shader's
        //       complete property set (every float/color/vector/texture + keywords +
        //       render queue) so ANY shader (Standard, MToon, Poiyomi, …) reconstructs
        //       exactly wherever that shader exists at load time. Textures are pooled at
        //       the doc level (shared textures stored once) in a trailing block.
        //   4 — adds an optional embedded source AssetBundle (the original .vsfavatar
        //       bytes) in a trailing block. The Reader can load it in-memory at runtime
        //       and harvest the REAL, fully-compiled material instances (e.g. locked
        //       Poiyomi, which can't be reconstructed from properties alone) and assign
        //       them to the rebuilt meshes by renderer name / submesh. Makes the .vbx
        //       truly self-contained for shaders absent from the load-time runtime.
        public const int Version = 4;

        public string avatarName = "avatar";
        public List<VbxBone> bones = new List<VbxBone>();
        public List<VbxHumanBone> humanoid = new List<VbxHumanBone>(); // optional
        public List<VbxMesh> meshes = new List<VbxMesh>();
        public List<VbxCollider> colliders = new List<VbxCollider>();  // v2+, optional
        // v3+ shared texture pool: PNG blobs referenced by index from material textures.
        public List<byte[]> textures = new List<byte[]>();
        // v4+ optional embedded source AssetBundle (.vsfavatar bytes), or null. Carries
        // the original compiled shaders/materials for faithful reconstruction of shaders
        // that don't exist in the load-time runtime (locked Poiyomi, etc.).
        public byte[] embeddedBundle;
    }

    /// <summary>
    /// A physics collider, attached to a bone (by name + index) in that bone's local
    /// space. Re-targetable to VRM spring-bone, DynamicBone, VRChat PhysBone,
    /// JayoPhysBones and JayoJiggle collider definitions by the consuming runtime.
    /// </summary>
    public sealed class VbxCollider
    {
        public string name;          // unique collider id
        public string boneName;      // transform it rides on (informational)
        public int boneIndex;        // index into VbxDoc.bones, or -1 for world-space
        public int shape;            // 0 = sphere, 1 = capsule, 2 = plane
        public Vector3 center;       // bone-local centre (sphere) / capsule end A / plane point
        public Vector3 centerEnd;    // bone-local capsule end B (== center when not a capsule)
        public Vector3 axis;         // bone-local capsule axis / plane normal
        public float radius;         // metres (sphere/capsule)
        public float height;         // capsule total height (informational; derivable from ends)
    }

    public sealed class VbxBone
    {
        public string name;
        public int parent;          // index into VbxDoc.bones, or -1
        public Vector3 localPos;
        public Quaternion localRot;
        public Vector3 localScale;
    }

    public sealed class VbxHumanBone
    {
        public int humanBone;       // (int)HumanBodyBones
        public int boneIndex;       // index into VbxDoc.bones
    }

    public sealed class VbxMesh
    {
        public string name;
        public int[] boneMap;       // renderer-local bone slot -> VbxDoc.bones index
        public int rootBone;        // VbxDoc.bones index of smr.rootBone, or -1

        public Vector3[] positions;
        public Vector3[] normals;   // length 0 if absent
        public Vector4[] tangents;  // length 0 if absent
        public Vector2[] uv;        // length 0 if absent
        public Vector2[] uv2;       // length 0 if absent
        public Matrix4x4[] bindposes;

        // unlimited skin weights, renderer-local bone slots
        public byte[] bonesPerVertex;     // length = vertex count
        public int[] influenceBone;       // flattened, renderer-local
        public float[] influenceWeight;   // flattened, parallel to influenceBone

        public int[][] submeshes;         // triangle index arrays
        public List<VbxBlendShape> blendShapes = new List<VbxBlendShape>();
        public List<VbxMaterial> materials = new List<VbxMaterial>();
    }

    public sealed class VbxBlendShape
    {
        public string name;
        public List<VbxBlendFrame> frames = new List<VbxBlendFrame>();
    }

    public sealed class VbxBlendFrame
    {
        public float weight;
        public Vector3[] dV;
        public Vector3[] dN;
        public Vector3[] dT;
    }

    public sealed class VbxMaterial
    {
        public string name;
        public string shader;
        // Legacy (v1/v2) best-effort fields — still written for backward compatibility
        // and used as a fallback when the original shader is unavailable at load time.
        public Color color = Color.white;
        public Color emission = Color.black;
        public float cutoff = 0.5f;
        public int renderQueue = -1;
        public byte[] mainTexPng;       // null if none
        public byte[] normalTexPng;     // null if none
        public byte[] emissionTexPng;   // null if none

        // v3 — full generic property capture. When the named shader exists at load
        // time these reconstruct the material exactly, for ANY shader.
        public string[] keywords;                                  // enabled shader keywords
        public List<VbxMatFloat>   floats   = new List<VbxMatFloat>();   // Float/Range/Int props
        public List<VbxMatColor>   colors   = new List<VbxMatColor>();   // Color props
        public List<VbxMatVector>  vectors  = new List<VbxMatVector>();  // Vector props
        public List<VbxMatTexture> texProps = new List<VbxMatTexture>(); // Texture props
    }

    public sealed class VbxMatFloat  { public string name; public float value; }
    public sealed class VbxMatColor  { public string name; public Color value; }
    public sealed class VbxMatVector { public string name; public Vector4 value; }
    public sealed class VbxMatTexture
    {
        public string name;          // shader property name (e.g. "_MainTex")
        public int texIndex;         // index into VbxDoc.textures, or -1 if no texture bound
        public Vector2 scale = Vector2.one;
        public Vector2 offset = Vector2.zero;
    }

    /// <summary>Binary (de)serializer. Little-endian via BinaryWriter/Reader.</summary>
    public static class VbxIO
    {
        public static void Write(Stream stream, VbxDoc doc)
        {
            var w = new BinaryWriter(stream, Encoding.UTF8);
            w.Write(VbxDoc.Magic);
            w.Write(VbxDoc.Version);
            w.Write(doc.avatarName ?? "");

            w.Write(doc.bones.Count);
            foreach (var b in doc.bones)
            {
                w.Write(b.name ?? "");
                w.Write(b.parent);
                WriteV3(w, b.localPos);
                WriteQ(w, b.localRot);
                WriteV3(w, b.localScale);
            }

            w.Write(doc.humanoid.Count);
            foreach (var h in doc.humanoid)
            {
                w.Write(h.humanBone);
                w.Write(h.boneIndex);
            }

            w.Write(doc.meshes.Count);
            foreach (var m in doc.meshes)
            {
                w.Write(m.name ?? "");
                WriteIntArray(w, m.boneMap);
                w.Write(m.rootBone);

                int vc = m.positions != null ? m.positions.Length : 0;
                w.Write(vc);
                WriteV3Array(w, m.positions, vc);
                WriteV3Array(w, m.normals, m.normals != null ? m.normals.Length : 0);
                WriteV4Array(w, m.tangents, m.tangents != null ? m.tangents.Length : 0);
                WriteV2Array(w, m.uv, m.uv != null ? m.uv.Length : 0);
                WriteV2Array(w, m.uv2, m.uv2 != null ? m.uv2.Length : 0);

                int bp = m.bindposes != null ? m.bindposes.Length : 0;
                w.Write(bp);
                for (int i = 0; i < bp; i++) WriteM4(w, m.bindposes[i]);

                // weights
                w.Write(m.bonesPerVertex != null ? m.bonesPerVertex.Length : 0);
                if (m.bonesPerVertex != null) w.Write(m.bonesPerVertex);
                int inf = m.influenceBone != null ? m.influenceBone.Length : 0;
                w.Write(inf);
                for (int i = 0; i < inf; i++)
                {
                    w.Write(m.influenceBone[i]);
                    w.Write(m.influenceWeight[i]);
                }

                // submeshes
                int sm = m.submeshes != null ? m.submeshes.Length : 0;
                w.Write(sm);
                for (int s = 0; s < sm; s++) WriteIntArray(w, m.submeshes[s]);

                // blendshapes
                w.Write(m.blendShapes.Count);
                foreach (var bs in m.blendShapes)
                {
                    w.Write(bs.name ?? "");
                    w.Write(bs.frames.Count);
                    foreach (var fr in bs.frames)
                    {
                        w.Write(fr.weight);
                        WriteV3Array(w, fr.dV, fr.dV != null ? fr.dV.Length : 0);
                        WriteV3Array(w, fr.dN, fr.dN != null ? fr.dN.Length : 0);
                        WriteV3Array(w, fr.dT, fr.dT != null ? fr.dT.Length : 0);
                    }
                }

                // materials
                w.Write(m.materials.Count);
                foreach (var mat in m.materials)
                {
                    w.Write(mat.name ?? "");
                    w.Write(mat.shader ?? "");
                    WriteColor(w, mat.color);
                    WriteColor(w, mat.emission);
                    w.Write(mat.cutoff);
                    w.Write(mat.renderQueue);
                    WriteBlob(w, mat.mainTexPng);
                    WriteBlob(w, mat.normalTexPng);
                    WriteBlob(w, mat.emissionTexPng);

                    // v3 generic property set
                    var kw = mat.keywords ?? new string[0];
                    w.Write(kw.Length);
                    for (int i = 0; i < kw.Length; i++) w.Write(kw[i] ?? "");

                    var mf = mat.floats ?? new List<VbxMatFloat>();
                    w.Write(mf.Count);
                    foreach (var f in mf) { w.Write(f.name ?? ""); w.Write(f.value); }

                    var mcl = mat.colors ?? new List<VbxMatColor>();
                    w.Write(mcl.Count);
                    foreach (var c in mcl) { w.Write(c.name ?? ""); WriteColor(w, c.value); }

                    var mv = mat.vectors ?? new List<VbxMatVector>();
                    w.Write(mv.Count);
                    foreach (var v in mv) { w.Write(v.name ?? ""); WriteV4(w, v.value); }

                    var mt = mat.texProps ?? new List<VbxMatTexture>();
                    w.Write(mt.Count);
                    foreach (var t in mt)
                    {
                        w.Write(t.name ?? "");
                        w.Write(t.texIndex);
                        WriteV2(w, t.scale);
                        WriteV2(w, t.offset);
                    }
                }
            }

            // colliders (v2+)
            var cols = doc.colliders ?? new List<VbxCollider>();
            w.Write(cols.Count);
            foreach (var c in cols)
            {
                w.Write(c.name ?? "");
                w.Write(c.boneName ?? "");
                w.Write(c.boneIndex);
                w.Write(c.shape);
                WriteV3(w, c.center);
                WriteV3(w, c.centerEnd);
                WriteV3(w, c.axis);
                w.Write(c.radius);
                w.Write(c.height);
            }

            // v3+ shared texture pool (trailing block). PNG blobs referenced by index
            // from VbxMatTexture.texIndex.
            var pool = doc.textures ?? new List<byte[]>();
            w.Write(pool.Count);
            foreach (var tex in pool) WriteBlob(w, tex);

            // v4+ embedded source AssetBundle (.vsfavatar bytes), or empty if none.
            WriteBlob(w, doc.embeddedBundle);

            w.Flush();
        }

        public static VbxDoc Read(Stream stream)
        {
            var r = new BinaryReader(stream, Encoding.UTF8);
            uint magic = r.ReadUInt32();
            if (magic != VbxDoc.Magic) throw new InvalidDataException("Not a VBX file (bad magic).");
            int ver = r.ReadInt32();
            if (ver < 1 || ver > VbxDoc.Version) throw new InvalidDataException("Unsupported VBX version " + ver + ".");

            var doc = new VbxDoc();
            doc.avatarName = r.ReadString();

            int bc = r.ReadInt32();
            for (int i = 0; i < bc; i++)
            {
                var b = new VbxBone();
                b.name = r.ReadString();
                b.parent = r.ReadInt32();
                b.localPos = ReadV3(r);
                b.localRot = ReadQ(r);
                b.localScale = ReadV3(r);
                doc.bones.Add(b);
            }

            int hc = r.ReadInt32();
            for (int i = 0; i < hc; i++)
                doc.humanoid.Add(new VbxHumanBone { humanBone = r.ReadInt32(), boneIndex = r.ReadInt32() });

            int mc = r.ReadInt32();
            for (int i = 0; i < mc; i++)
            {
                var m = new VbxMesh();
                m.name = r.ReadString();
                m.boneMap = ReadIntArray(r);
                m.rootBone = r.ReadInt32();

                int vc = r.ReadInt32();
                m.positions = ReadV3Array(r);
                m.normals = ReadV3Array(r);
                m.tangents = ReadV4Array(r);
                m.uv = ReadV2Array(r);
                m.uv2 = ReadV2Array(r);

                int bp = r.ReadInt32();
                m.bindposes = new Matrix4x4[bp];
                for (int k = 0; k < bp; k++) m.bindposes[k] = ReadM4(r);

                int bpvLen = r.ReadInt32();
                m.bonesPerVertex = bpvLen > 0 ? r.ReadBytes(bpvLen) : new byte[0];
                int inf = r.ReadInt32();
                m.influenceBone = new int[inf];
                m.influenceWeight = new float[inf];
                for (int k = 0; k < inf; k++)
                {
                    m.influenceBone[k] = r.ReadInt32();
                    m.influenceWeight[k] = r.ReadSingle();
                }

                int sm = r.ReadInt32();
                m.submeshes = new int[sm][];
                for (int s = 0; s < sm; s++) m.submeshes[s] = ReadIntArray(r);

                int bsc = r.ReadInt32();
                for (int s = 0; s < bsc; s++)
                {
                    var bs = new VbxBlendShape { name = r.ReadString() };
                    int fc = r.ReadInt32();
                    for (int f = 0; f < fc; f++)
                    {
                        var fr = new VbxBlendFrame();
                        fr.weight = r.ReadSingle();
                        fr.dV = ReadV3Array(r);
                        fr.dN = ReadV3Array(r);
                        fr.dT = ReadV3Array(r);
                        bs.frames.Add(fr);
                    }
                    m.blendShapes.Add(bs);
                }

                int matc = r.ReadInt32();
                for (int s = 0; s < matc; s++)
                {
                    var mat = new VbxMaterial();
                    mat.name = r.ReadString();
                    mat.shader = r.ReadString();
                    mat.color = ReadColor(r);
                    mat.emission = ReadColor(r);
                    mat.cutoff = r.ReadSingle();
                    mat.renderQueue = r.ReadInt32();
                    mat.mainTexPng = ReadBlob(r);
                    mat.normalTexPng = ReadBlob(r);
                    mat.emissionTexPng = ReadBlob(r);

                    if (ver >= 3)
                    {
                        int kwn = r.ReadInt32();
                        mat.keywords = new string[kwn];
                        for (int k = 0; k < kwn; k++) mat.keywords[k] = r.ReadString();

                        int fn = r.ReadInt32();
                        for (int k = 0; k < fn; k++)
                            mat.floats.Add(new VbxMatFloat { name = r.ReadString(), value = r.ReadSingle() });

                        int cn = r.ReadInt32();
                        for (int k = 0; k < cn; k++)
                            mat.colors.Add(new VbxMatColor { name = r.ReadString(), value = ReadColor(r) });

                        int vn = r.ReadInt32();
                        for (int k = 0; k < vn; k++)
                            mat.vectors.Add(new VbxMatVector { name = r.ReadString(), value = ReadV4(r) });

                        int tn = r.ReadInt32();
                        for (int k = 0; k < tn; k++)
                        {
                            var t = new VbxMatTexture();
                            t.name = r.ReadString();
                            t.texIndex = r.ReadInt32();
                            t.scale = ReadV2(r);
                            t.offset = ReadV2(r);
                            mat.texProps.Add(t);
                        }
                    }
                    m.materials.Add(mat);
                }

                doc.meshes.Add(m);
            }

            // colliders (v2+); absent in v1 files
            if (ver >= 2)
            {
                int cc = r.ReadInt32();
                for (int i = 0; i < cc; i++)
                {
                    var c = new VbxCollider();
                    c.name = r.ReadString();
                    c.boneName = r.ReadString();
                    c.boneIndex = r.ReadInt32();
                    c.shape = r.ReadInt32();
                    c.center = ReadV3(r);
                    c.centerEnd = ReadV3(r);
                    c.axis = ReadV3(r);
                    c.radius = r.ReadSingle();
                    c.height = r.ReadSingle();
                    doc.colliders.Add(c);
                }
            }

            // v3+ shared texture pool (trailing block)
            if (ver >= 3)
            {
                int tpn = r.ReadInt32();
                for (int i = 0; i < tpn; i++) doc.textures.Add(ReadBlob(r));
            }

            // v4+ embedded source AssetBundle (.vsfavatar bytes)
            if (ver >= 4)
                doc.embeddedBundle = ReadBlob(r);

            return doc;
        }

        // ---- primitives ----
        static void WriteV2(BinaryWriter w, Vector2 v) { w.Write(v.x); w.Write(v.y); }
        static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void WriteV4(BinaryWriter w, Vector4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
        static void WriteQ(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        static void WriteColor(BinaryWriter w, Color c) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
        static void WriteM4(BinaryWriter w, Matrix4x4 m) { for (int i = 0; i < 16; i++) w.Write(m[i]); }

        static Vector2 ReadV2(BinaryReader r) { return new Vector2(r.ReadSingle(), r.ReadSingle()); }
        static Vector3 ReadV3(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static Vector4 ReadV4(BinaryReader r) { return new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static Quaternion ReadQ(BinaryReader r) { return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static Color ReadColor(BinaryReader r) { return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static Matrix4x4 ReadM4(BinaryReader r) { var m = new Matrix4x4(); for (int i = 0; i < 16; i++) m[i] = r.ReadSingle(); return m; }

        static void WriteIntArray(BinaryWriter w, int[] a)
        {
            int n = a != null ? a.Length : 0;
            w.Write(n);
            for (int i = 0; i < n; i++) w.Write(a[i]);
        }
        static int[] ReadIntArray(BinaryReader r)
        {
            int n = r.ReadInt32();
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadInt32();
            return a;
        }

        static void WriteV2Array(BinaryWriter w, Vector2[] a, int n) { w.Write(n); for (int i = 0; i < n; i++) WriteV2(w, a[i]); }
        static void WriteV3Array(BinaryWriter w, Vector3[] a, int n) { w.Write(n); for (int i = 0; i < n; i++) WriteV3(w, a[i]); }
        static void WriteV4Array(BinaryWriter w, Vector4[] a, int n) { w.Write(n); for (int i = 0; i < n; i++) WriteV4(w, a[i]); }
        static Vector2[] ReadV2Array(BinaryReader r) { int n = r.ReadInt32(); var a = new Vector2[n]; for (int i = 0; i < n; i++) a[i] = ReadV2(r); return a; }
        static Vector3[] ReadV3Array(BinaryReader r) { int n = r.ReadInt32(); var a = new Vector3[n]; for (int i = 0; i < n; i++) a[i] = ReadV3(r); return a; }
        static Vector4[] ReadV4Array(BinaryReader r) { int n = r.ReadInt32(); var a = new Vector4[n]; for (int i = 0; i < n; i++) a[i] = ReadV4(r); return a; }

        static void WriteBlob(BinaryWriter w, byte[] b)
        {
            int n = b != null ? b.Length : 0;
            w.Write(n);
            if (n > 0) w.Write(b);
        }
        static byte[] ReadBlob(BinaryReader r)
        {
            int n = r.ReadInt32();
            return n > 0 ? r.ReadBytes(n) : null;
        }
    }
}
