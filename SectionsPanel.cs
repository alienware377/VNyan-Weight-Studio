using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Collapsible sections for the main window, same pattern as the SoftBody Suite:
    /// a "Hdr_*" label starts a section and every row until the next header belongs
    /// to it. The header becomes a click-to-fold button with a [+]/[-] marker; a shut
    /// section pulls everything below it up and shrinks the scroll content to match.
    /// Rows above the first header (status line etc.) stay put.
    /// </summary>
    public partial class BoneWeightStudioPlugin
    {
        class UiSection
        {
            public string label;
            public Text hdr;
            public readonly List<RectTransform> rows = new List<RectTransform>();
            public readonly List<float> rowY = new List<float>();
            public float hdrY, height;
            public bool open;
            public string baseText = "";
        }
        readonly List<UiSection> uiSections = new List<UiSection>();
        RectTransform uiSectionContent;
        float uiSectionContentH;

        static string FoldMarker(bool open) { return open ? "[-] " : "[+] "; }

        // sections worth having open when the window first appears
        static bool SectionOpenByDefault(string label)
        {
            return label == "Meshes" || label == "Bones";
        }

        void BuildSections()
        {
            uiSections.Clear();
            RectTransform content = Find<RectTransform>("BodyContent");
            if (content == null) return;
            uiSectionContent = content;
            uiSectionContentH = content.sizeDelta.y;

            var kids = new List<RectTransform>();
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform rt = content.GetChild(i) as RectTransform;
                if (rt != null) kids.Add(rt);
            }
            // visual order: anchoredPosition y runs negative downwards. A header shares its
            // row with buttons (Meshes + Rescan); List.Sort is unstable, so on a tie the header
            // must come first, or the button joins the section ABOVE and folding that one
            // pulls the next header up over it.
            kids.Sort((a, b) =>
            {
                float d = b.anchoredPosition.y - a.anchoredPosition.y;
                if (Mathf.Abs(d) > 0.5f) return d > 0f ? 1 : -1;
                bool ha = a.name.StartsWith("Hdr_"), hb = b.name.StartsWith("Hdr_");
                if (ha != hb) return ha ? -1 : 1;
                return 0;
            });

            UiSection cur = null;
            for (int i = 0; i < kids.Count; i++)
            {
                RectTransform rt = kids[i];
                if (rt.name.StartsWith("Hdr_"))
                {
                    cur = new UiSection();
                    cur.label = rt.name.Substring(4);
                    cur.hdr = rt.GetComponent<Text>();
                    cur.hdrY = rt.anchoredPosition.y;
                    cur.open = SectionOpenByDefault(cur.label);
                    uiSections.Add(cur);
                    continue;
                }
                if (cur == null) continue;
                cur.rows.Add(rt);
                cur.rowY.Add(rt.anchoredPosition.y);
            }

            for (int s = 0; s < uiSections.Count; s++)
            {
                UiSection sec = uiSections[s];
                float bottom = sec.hdrY;
                for (int r = 0; r < sec.rows.Count; r++)
                {
                    float b = sec.rowY[r] - sec.rows[r].rect.height;
                    if (b < bottom) bottom = b;
                }
                sec.height = sec.hdrY - bottom;

                if (sec.hdr != null)
                {
                    UiSection captured = sec;
                    Button btn = sec.hdr.gameObject.GetComponent<Button>();
                    if (btn == null) btn = sec.hdr.gameObject.AddComponent<Button>();
                    btn.transition = Selectable.Transition.None;
                    btn.onClick.AddListener(() => ToggleSection(captured));
                    sec.baseText = sec.hdr.text;
                    sec.hdr.text = FoldMarker(sec.open) + sec.baseText;
                }
            }
            RelayoutSections();
        }

        void ToggleSection(UiSection sec)
        {
            sec.open = !sec.open;
            if (sec.hdr != null) sec.hdr.text = FoldMarker(sec.open) + sec.baseText;
            RelayoutSections();
        }

        // rows keep their built positions; a shut section just pulls everything below it up
        void RelayoutSections()
        {
            float shift = 0f;
            for (int s = 0; s < uiSections.Count; s++)
            {
                UiSection sec = uiSections[s];
                if (sec.hdr != null)
                {
                    RectTransform h = sec.hdr.rectTransform;
                    h.anchoredPosition = new Vector2(h.anchoredPosition.x, sec.hdrY + shift);
                }
                for (int r = 0; r < sec.rows.Count; r++)
                {
                    RectTransform rt = sec.rows[r];
                    if (rt == null) continue;
                    bool vis = sec.open;
                    if (rt.gameObject.activeSelf != vis) rt.gameObject.SetActive(vis);
                    if (vis) rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, sec.rowY[r] + shift);
                }
                if (!sec.open) shift += Mathf.Max(0f, sec.height - 24f);
            }
            if (uiSectionContent != null)
                uiSectionContent.sizeDelta = new Vector2(uiSectionContent.sizeDelta.x,
                    Mathf.Max(60f, uiSectionContentH - shift));
        }
    }
}
