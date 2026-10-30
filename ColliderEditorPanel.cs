using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Collider Editor window: select a generated collider (from the list or by clicking
    /// it in the 3D view), then move / resize / rotate it, switch sphere-capsule, add,
    /// duplicate or delete. Edits mutate <c>genColliders</c> in place so every exporter
    /// picks them up unchanged.
    ///
    /// Sliders work relative to a "base pose" captured when a collider is selected:
    /// offset and rotation sliders start at zero and displace/rotate from that base, so
    /// re-selecting always gives clean zeroed handles.
    /// </summary>
    public partial class BoneWeightStudioPlugin
    {
        GameObject colEdWindow;
        int colEdSel = -1;
        bool colEdPick;
        bool colEdSuppress;              // true while pushing values INTO sliders

        Text colEdStatus;
        Text colEdSelLabel;
        Text colEdTypeText;
        RectTransform colEdContent;

        Slider cedRadius, cedLen, cedPosX, cedPosY, cedPosZ, cedRotX, cedRotY, cedRotZ;

        // base pose of the selected collider (captured on selection)
        Vector3 cedBaseMid;
        Vector3 cedBaseAxis = Vector3.up;

        // undo/redo: whole-list snapshots (colliders are tiny, so this is cheap).
        // Continuous slider drags coalesce into one step via the time gate.
        readonly List<List<ColliderGen.GenCollider>> colEdUndoStack = new List<List<ColliderGen.GenCollider>>();
        readonly List<List<ColliderGen.GenCollider>> colEdRedoStack = new List<List<ColliderGen.GenCollider>>();
        float colEdLastEditTime = -999f;
        const int ColEdMaxUndo = 50;

        void ColEdInit()
        {
            if (colliderEditorPrefab == null) return;
            colEdWindow = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(colliderEditorPrefab);
            if (colEdWindow == null) return;
            colEdWindow.GetComponent<RectTransform>().anchoredPosition = new Vector2(250f, 0f);
            colEdWindow.SetActive(false);

            colEdStatus = FindIn<Text>(colEdWindow, "Label_ColEdStatus");
            colEdSelLabel = FindIn<Text>(colEdWindow, "Label_ColEdSel");
            colEdContent = FindIn<RectTransform>(colEdWindow, "ColEdContent");

            Toggle pick = FindIn<Toggle>(colEdWindow, "Toggle_ColEdPick");
            if (pick != null)
            {
                pick.SetIsOnWithoutNotify(false);
                pick.onValueChanged.AddListener(v =>
                {
                    colEdPick = v;
                    ColEdSetStatus(v ? "Pick ON: click a collider on the model to select it."
                                     : "Pick off.");
                });
            }

            cedRadius = ColEdBindSlider("cedradius", "0.000");
            cedLen    = ColEdBindSlider("cedlen",    "0.000");
            cedPosX   = ColEdBindSlider("cedposx",   "0.000");
            cedPosY   = ColEdBindSlider("cedposy",   "0.000");
            cedPosZ   = ColEdBindSlider("cedposz",   "0.000");
            cedRotX   = ColEdBindSlider("cedrotx",   "0");
            cedRotY   = ColEdBindSlider("cedroty",   "0");
            cedRotZ   = ColEdBindSlider("cedrotz",   "0");

            Button tb = FindIn<Button>(colEdWindow, "Button_ColEdType");
            if (tb != null) { tb.onClick.AddListener(ColEdSwitchType); colEdTypeText = tb.GetComponentInChildren<Text>(); }
            ColEdBindButton("Button_ColEdDup", ColEdDuplicate);
            ColEdBindButton("Button_ColEdDel", ColEdDelete);
            ColEdBindButton("Button_ColEdAdd", ColEdAdd);
            ColEdBindButton("Button_ColEdUndo", ColEdUndoOp);
            ColEdBindButton("Button_ColEdRedo", ColEdRedoOp);
            ColEdBindButton("Button_ColEdClose", () => { if (colEdWindow != null) colEdWindow.SetActive(false); });

            // shared X at the top-right does what the bottom Close did; that Close is hidden and
            // "Add on Active Bone" takes the whole last row
            RectTransform cedRt = colEdWindow.GetComponent<RectTransform>();
            PluginChrome.EnsureCloseX(cedRt, () => { if (colEdWindow != null) colEdWindow.SetActive(false); }, uiFont);
            PluginChrome.HideOtherClose(cedRt, "Button_ColEdClose");
        }

        // ------------------------------------------------------------ open/close

        void ColEdToggle()
        {
            if (colEdWindow == null) { SetStatus("Collider editor window missing. Rebuild the .vnobj."); return; }
            bool show = !colEdWindow.activeSelf;
            colEdWindow.SetActive(show);
            if (show)
            {
                colEdWindow.transform.SetAsLastSibling();
                ColEdEnsureData();
                ColEdRebuildList();
                ColEdSetStatus(genColliders != null && genColliders.Count > 0
                    ? genColliders.Count + " collider(s). Click one in the list or enable Pick."
                    : "No colliders yet. Generate in the main window, or Add on the active bone.");
            }
        }

        void ColEdEnsureData()
        {
            if (genColliders == null) genColliders = new List<ColliderGen.GenCollider>();
            if (colliderAvatarRoot == null)
            {
                Transform root;
                GatherAvatarRenderers(out root);
                colliderAvatarRoot = root;
                RebuildColliderBoneCache();
            }
        }

        void ColEdOnCollidersChanged()
        {
            colEdSel = -1;
            if (colEdWindow != null && colEdWindow.activeSelf) ColEdRebuildList();
        }

        void ColEdSetStatus(string s) { if (colEdStatus != null) colEdStatus.text = s; }

        // ------------------------------------------------------------ list

        void ColEdRebuildList()
        {
            if (colEdContent == null) return;
            for (int i = colEdContent.childCount - 1; i >= 0; i--)
                Destroy(colEdContent.GetChild(i).gameObject);

            float y = 0f;
            const float h = 20f;
            int n = genColliders != null ? genColliders.Count : 0;
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                ColliderGen.GenCollider c = genColliders[i];
                string tag = c.shape == ColliderGen.GenShape.Sphere ? "[sph] " : "[cap] ";
                MakeRow(colEdContent, "Row_" + i, tag + c.name + "   (" + c.boneName + ")",
                    y, h, () => ColEdSelect(idx), () => colEdSel == idx);
                y += h;
            }
            colEdContent.sizeDelta = new Vector2(0f, y);
            ColEdUpdateSelLabel();
        }

        void ColEdUpdateSelLabel()
        {
            if (colEdSelLabel == null) return;
            if (colEdSel < 0 || genColliders == null || colEdSel >= genColliders.Count)
            {
                colEdSelLabel.text = "nothing selected";
                if (colEdTypeText != null) colEdTypeText.text = "Sphere <-> Capsule";
                return;
            }
            ColliderGen.GenCollider c = genColliders[colEdSel];
            colEdSelLabel.text = "Selected: " + c.name + " on " + c.boneName +
                " (" + (c.shape == ColliderGen.GenShape.Sphere ? "sphere" : "capsule") + ")";
            if (colEdTypeText != null)
                colEdTypeText.text = c.shape == ColliderGen.GenShape.Sphere ? "Make Capsule" : "Make Sphere";
        }

        // ------------------------------------------------------------ selection

        void ColEdSelect(int idx)
        {
            if (genColliders == null || idx < 0 || idx >= genColliders.Count) return;
            colEdSel = idx;
            ColliderGen.GenCollider c = genColliders[idx];

            // capture base pose; sliders become deltas from here
            cedBaseMid = c.Mid;
            cedBaseAxis = c.Axis;
            float len = (c.centerEnd - c.center).magnitude;

            colEdSuppress = true;
            ColEdSetSlider(cedRadius, c.radius, "0.000");
            ColEdSetSlider(cedLen, len, "0.000");
            ColEdSetSlider(cedPosX, 0f, "0.000");
            ColEdSetSlider(cedPosY, 0f, "0.000");
            ColEdSetSlider(cedPosZ, 0f, "0.000");
            ColEdSetSlider(cedRotX, 0f, "0");
            ColEdSetSlider(cedRotY, 0f, "0");
            ColEdSetSlider(cedRotZ, 0f, "0");
            colEdSuppress = false;

            ColEdRebuildList();
            ColEdSetStatus("Editing " + c.name + ": drag sliders; shape updates live.");
        }

        // ------------------------------------------------------------ undo / redo

        List<ColliderGen.GenCollider> ColEdSnapshot()
        {
            var s = new List<ColliderGen.GenCollider>(genColliders.Count);
            for (int i = 0; i < genColliders.Count; i++)
            {
                ColliderGen.GenCollider c = genColliders[i];
                s.Add(new ColliderGen.GenCollider
                {
                    name = c.name, boneName = c.boneName, shape = c.shape,
                    center = c.center, centerEnd = c.centerEnd,
                    radius = c.radius, vertexCount = c.vertexCount
                });
            }
            return s;
        }

        void ColEdPushUndo()
        {
            if (genColliders == null) return;
            colEdUndoStack.Add(ColEdSnapshot());
            if (colEdUndoStack.Count > ColEdMaxUndo) colEdUndoStack.RemoveAt(0);
            colEdRedoStack.Clear();
        }

        void ColEdUndoOp()
        {
            if (colEdUndoStack.Count == 0) { ColEdSetStatus("Nothing to undo."); return; }
            if (genColliders == null) genColliders = new List<ColliderGen.GenCollider>();
            colEdRedoStack.Add(ColEdSnapshot());
            genColliders = colEdUndoStack[colEdUndoStack.Count - 1];
            colEdUndoStack.RemoveAt(colEdUndoStack.Count - 1);
            ColEdAfterHistory("Undid, " + colEdUndoStack.Count + " step(s) left.");
        }

        void ColEdRedoOp()
        {
            if (colEdRedoStack.Count == 0) { ColEdSetStatus("Nothing to redo."); return; }
            if (genColliders == null) genColliders = new List<ColliderGen.GenCollider>();
            colEdUndoStack.Add(ColEdSnapshot());
            genColliders = colEdRedoStack[colEdRedoStack.Count - 1];
            colEdRedoStack.RemoveAt(colEdRedoStack.Count - 1);
            ColEdAfterHistory("Redid, " + colEdRedoStack.Count + " step(s) left.");
        }

        void ColEdAfterHistory(string msg)
        {
            colEdSel = -1;
            colEdLastEditTime = -999f;
            UpdateColliderLabel();
            ColEdRebuildList();
            ColEdSetStatus(msg);
        }

        // ------------------------------------------------------------ apply edits

        void ColEdApply()
        {
            if (colEdSel < 0 || genColliders == null || colEdSel >= genColliders.Count) return;

            // coalesce a continuous drag into one undo step: only push when the last
            // slider edit was a while ago
            if (Time.unscaledTime - colEdLastEditTime > 0.8f) ColEdPushUndo();
            colEdLastEditTime = Time.unscaledTime;

            ColliderGen.GenCollider c = genColliders[colEdSel];

            Vector3 mid = cedBaseMid + new Vector3(
                cedPosX != null ? cedPosX.value : 0f,
                cedPosY != null ? cedPosY.value : 0f,
                cedPosZ != null ? cedPosZ.value : 0f);

            if (cedRadius != null) c.radius = cedRadius.value;

            if (c.shape == ColliderGen.GenShape.Capsule)
            {
                Vector3 axis = Quaternion.Euler(
                    cedRotX != null ? cedRotX.value : 0f,
                    cedRotY != null ? cedRotY.value : 0f,
                    cedRotZ != null ? cedRotZ.value : 0f) * cedBaseAxis;
                if (axis.sqrMagnitude < 1e-10f) axis = Vector3.up;
                axis.Normalize();
                float half = Mathf.Max(0f, cedLen != null ? cedLen.value : 0f) * 0.5f;
                c.center = mid - axis * half;
                c.centerEnd = mid + axis * half;
            }
            else
            {
                c.center = mid;
                c.centerEnd = mid;
            }
        }

        // ------------------------------------------------------------ buttons

        void ColEdSwitchType()
        {
            if (colEdSel < 0 || genColliders == null || colEdSel >= genColliders.Count)
            { ColEdSetStatus("Select a collider first."); return; }
            ColEdPushUndo();
            colEdLastEditTime = -999f;
            ColliderGen.GenCollider c = genColliders[colEdSel];

            if (c.shape == ColliderGen.GenShape.Sphere)
            {
                c.shape = ColliderGen.GenShape.Capsule;
                Vector3 mid = c.Mid;
                float half = Mathf.Max(c.radius, 0.01f);
                c.center = mid - Vector3.up * half;
                c.centerEnd = mid + Vector3.up * half;
            }
            else
            {
                Vector3 mid = c.Mid;
                c.shape = ColliderGen.GenShape.Sphere;
                c.center = mid;
                c.centerEnd = mid;
            }
            ColEdSelect(colEdSel);   // re-capture base + refresh sliders/labels
        }

        void ColEdDuplicate()
        {
            if (colEdSel < 0 || genColliders == null || colEdSel >= genColliders.Count)
            { ColEdSetStatus("Select a collider first."); return; }
            ColEdPushUndo();
            colEdLastEditTime = -999f;
            ColliderGen.GenCollider s = genColliders[colEdSel];
            ColliderGen.GenCollider c = new ColliderGen.GenCollider
            {
                name = ColEdUniqueName(s.name + "_copy"),
                boneName = s.boneName,
                shape = s.shape,
                center = s.center,
                centerEnd = s.centerEnd,
                radius = s.radius,
                vertexCount = s.vertexCount
            };
            genColliders.Insert(colEdSel + 1, c);
            UpdateColliderLabel();
            ColEdSelect(colEdSel + 1);
        }

        void ColEdDelete()
        {
            if (colEdSel < 0 || genColliders == null || colEdSel >= genColliders.Count)
            { ColEdSetStatus("Select a collider first."); return; }
            ColEdPushUndo();
            colEdLastEditTime = -999f;
            string nm = genColliders[colEdSel].name;
            genColliders.RemoveAt(colEdSel);
            colEdSel = -1;
            UpdateColliderLabel();
            ColEdRebuildList();
            ColEdSetStatus("Deleted " + nm + ".");
        }

        void ColEdAdd()
        {
            ColEdEnsureData();
            string bone = null;
            if (model != null && brush.activeBone >= 0 && brush.activeBone < model.boneNames.Length)
                bone = model.boneNames[brush.activeBone];
            if (string.IsNullOrEmpty(bone))
            { ColEdSetStatus("Pick a bone in the main window's bone list first."); return; }

            ColEdPushUndo();
            colEdLastEditTime = -999f;
            ColliderGen.GenCollider c = new ColliderGen.GenCollider
            {
                name = ColEdUniqueName(bone + "_col"),
                boneName = bone,
                shape = ColliderGen.GenShape.Sphere,
                center = Vector3.zero,
                centerEnd = Vector3.zero,
                radius = 0.05f
            };
            genColliders.Add(c);
            UpdateColliderLabel();
            ColEdSelect(genColliders.Count - 1);
            ColEdSetStatus("Added " + c.name + " at " + bone + " origin. Move it with the offset sliders.");
        }

        string ColEdUniqueName(string baseName)
        {
            var used = new HashSet<string>();
            for (int i = 0; i < genColliders.Count; i++)
                if (!string.IsNullOrEmpty(genColliders[i].name)) used.Add(genColliders[i].name);
            string n = baseName;
            int k = 1;
            while (used.Contains(n)) n = baseName + "_" + (k++);
            return n;
        }

        // ------------------------------------------------------------ 3D-view picking

        // Returns true when it consumed the click, so paint doesn't also fire.
        bool ColEdUpdate()
        {
            if (colEdWindow == null || !colEdWindow.activeSelf) return false;

            // hotkeys while the editor is open: Ctrl+Z undo, Ctrl+Shift+Z / Ctrl+Y redo
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.Z))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (shift) ColEdRedoOp(); else ColEdUndoOp();
            }
            else if (ctrl && Input.GetKeyDown(KeyCode.Y))
            {
                ColEdRedoOp();
            }

            if (!colEdPick) return false;
            if (!Input.GetMouseButtonDown(0)) return false;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return false;
            if (genColliders == null || genColliders.Count == 0 || colliderBoneCache == null) return false;

            EnsureCamera();
            if (cam == null) return false;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            int best = -1;
            float bestT = float.MaxValue;
            for (int i = 0; i < genColliders.Count; i++)
            {
                ColliderGen.GenCollider c = genColliders[i];
                Transform bone;
                colliderBoneCache.TryGetValue(c.boneName, out bone);
                Vector3 a = bone != null ? bone.TransformPoint(c.center) : c.center;
                Vector3 b = bone != null ? bone.TransformPoint(c.centerEnd) : c.centerEnd;

                float t;
                if (RayHitsCapsule(ray, a, b, c.radius, out t) && t < bestT) { bestT = t; best = i; }
            }

            if (best >= 0)
            {
                ColEdSelect(best);
                return true;   // consumed: don't paint on the same click
            }
            ColEdSetStatus("No collider under the cursor.");
            return true;       // pick mode still eats the click so paint doesn't fire
        }

        // Ray vs capsule (degenerates to sphere when a==b): closest approach between the
        // ray and the segment, hit when within radius. t is the distance along the ray.
        static bool RayHitsCapsule(Ray ray, Vector3 a, Vector3 b, float radius, out float t)
        {
            t = 0f;
            Vector3 d = ray.direction;               // unit length
            Vector3 seg = b - a;
            float segLen2 = seg.sqrMagnitude;

            if (segLen2 < 1e-12f)
            {
                float tc = Vector3.Dot(a - ray.origin, d);
                if (tc < 0f) return false;
                Vector3 p = ray.origin + d * tc;
                if ((p - a).sqrMagnitude > radius * radius) return false;
                t = tc;
                return true;
            }

            // closest points between the ray (o + t*d, t>=0) and segment (a + s*seg, s in [0,1])
            Vector3 w = ray.origin - a;
            float bDS = Vector3.Dot(d, seg);
            float dDW = Vector3.Dot(d, w);
            float sDW = Vector3.Dot(seg, w);
            float denom = segLen2 - bDS * bDS;       // (d·d)(seg·seg) - (d·seg)^2 with d unit

            float s = denom > 1e-9f ? Mathf.Clamp01((sDW - bDS * dDW) / denom) : 0f;
            float tr = bDS * s - dDW;                // ray param of closest approach to a+s*seg
            if (tr < 0f) tr = 0f;

            Vector3 onSeg = a + seg * s;
            Vector3 onRay = ray.origin + d * tr;
            if ((onRay - onSeg).sqrMagnitude > radius * radius) return false;
            t = tr;
            return true;
        }

        // ------------------------------------------------------------ small UI helpers

        Slider ColEdBindSlider(string key, string fmt)
        {
            Slider s = FindIn<Slider>(colEdWindow, "Slider_" + key);
            Text v = FindIn<Text>(colEdWindow, "Value_" + key);
            if (s == null) return null;
            s.onValueChanged.AddListener(val =>
            {
                if (v != null) v.text = val.ToString(fmt, CultureInfo.InvariantCulture);
                if (!colEdSuppress) ColEdApply();
            });
            return s;
        }

        static void ColEdSetSlider(Slider s, float v, string fmt)
        {
            if (s == null) return;
            s.SetValueWithoutNotify(v);
            // keep the value text in sync even though no event fires
            Transform p = s.transform.parent;
            // value label lives beside the slider; find by matching name convention
            string valName = s.gameObject.name.Replace("Slider_", "Value_");
            if (p != null)
            {
                foreach (Text t in p.GetComponentsInChildren<Text>(true))
                    if (t.gameObject.name == valName)
                    { t.text = v.ToString(fmt, CultureInfo.InvariantCulture); break; }
            }
        }

        void ColEdBindButton(string name, UnityEngine.Events.UnityAction action)
        {
            Button b = FindIn<Button>(colEdWindow, name);
            if (b != null) b.onClick.AddListener(action);
        }

        T FindIn<T>(GameObject rootGo, string name) where T : Component
        {
            if (rootGo == null) return null;
            foreach (T c in rootGo.GetComponentsInChildren<T>(true))
                if (c.gameObject.name == name) return c;
            return null;
        }
    }
}
