using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Mirror Weights UI: axis pick, exact/projection method, mix slider, and the
    /// three mirror actions (bone to L/R counterpart, self +side to -side, self
    /// -side to +side). Projection mode requires a still, symmetric pose, so a
    /// short motion check runs first and blocks the transfer while anything is
    /// moving (live tracking, animations, Pose Studio playback).
    /// </summary>
    public partial class BoneWeightStudioPlugin
    {
        int mirAxis;                 // 0=X 1=Y 2=Z
        bool mirProjection;
        float mirMix = 1f;           // 0..1
        Text mirAxisText;
        bool mirBusy;

        const string MirWarn =
            "Projection mirroring: put the model in a SYMMETRIC pose with no parts pressed " +
            "into each other (no inner thighs or arms clipping), pause Pose Studio movement " +
            "and VNyan animations. Still poses are fine; live tracking is not.";

        void WireMirrorUI()
        {
            BindToggle("Toggle_mirproj", mirProjection, v =>
            {
                mirProjection = v;
                SetStatus(v ? MirWarn : "Mirror method: exact verts, matched in the model's default (bind) position.");
            });
            BindSliderFloat("Slider_mirmix", "Value_mirmix", mirMix * 100f, 0f, 100f, v => mirMix = v / 100f, "0");

            Button ax = Find<Button>("Button_MirAxis");
            if (ax != null)
            {
                mirAxisText = ax.GetComponentInChildren<Text>();
                ax.onClick.AddListener(() =>
                {
                    mirAxis = (mirAxis + 1) % 3;
                    if (mirAxisText != null) mirAxisText.text = "Axis: " + "XYZ"[mirAxis];
                    SetStatus("Mirror axis: " + "XYZ"[mirAxis] + " (X is the usual left/right).");
                });
                if (mirAxisText != null) mirAxisText.text = "Axis: X";
            }

            BindButton("Button_MirrorBone", () => MirrorStart(0));
            BindButton("Button_MirSelfPM", () => MirrorStart(1));   // + side -> - side
            BindButton("Button_MirSelfMP", () => MirrorStart(2));   // - side -> + side
        }

        void MirrorStart(int op)
        {
            if (mirBusy) { SetStatus("Mirror already running - one moment."); return; }
            if (model == null || !model.Loaded) { SetStatus("Load a mesh first."); return; }
            if (brush.activeBone < 0 || brush.activeBone >= model.boneNames.Length)
            { SetStatus("Pick a bone in the bone list first."); return; }

            if (op == 0)
            {
                int other = WeightMirror.FindCounterpart(model.boneNames, brush.activeBone);
                if (other < 0)
                {
                    SetStatus("No L/R counterpart found for '" + model.boneNames[brush.activeBone] +
                        "' (looked for _L/_R, .L/.R, Left/Right...).");
                    return;
                }
            }

            if (mirProjection) StartCoroutine(MirrorMotionCheckThenRun(op));
            else MirrorRun(op);
        }

        // Watch a spread of vertices for ~a third of a second; if anything drifts,
        // tracking/animation is live and projection would smear - block it.
        IEnumerator MirrorMotionCheckThenRun(int op)
        {
            mirBusy = true;
            SetStatus("Checking the model is holding still...");
            model.RefreshWorldPositions(true);
            int step = Mathf.Max(1, model.weldCount / 64);
            var before = new List<Vector3>(70);
            for (int w = 0; w < model.weldCount; w += step) before.Add(model.weldWorldPos[w]);

            yield return new WaitForSecondsRealtime(0.35f);

            model.RefreshWorldPositions(true);
            float worst = 0f;
            int i = 0;
            for (int w = 0; w < model.weldCount && i < before.Count; w += step, i++)
            {
                float d = (model.weldWorldPos[w] - before[i]).magnitude;
                if (d > worst) worst = d;
            }
            mirBusy = false;

            if (worst > 0.002f)
            {
                SetStatus("Movement detected (" + (worst * 1000f).ToString("0.#") +
                    " mm) - pause tracking/animations/Pose Studio playback, hold still, then click again.");
                yield break;
            }
            MirrorRun(op);
        }

        void MirrorRun(int op)
        {
            try
            {
                Transform root;
                GatherAvatarRenderers(out root);

                model.PushUndo();
                int changed, unmatched;
                string what;

                if (op == 0)
                {
                    int other = WeightMirror.FindCounterpart(model.boneNames, brush.activeBone);
                    changed = WeightMirror.MirrorBoneToBone(model, brush.activeBone, other,
                        mirAxis, mirProjection, mirMix, root, out unmatched);
                    what = "'" + model.boneNames[brush.activeBone] + "' -> '" + model.boneNames[other] + "'";
                }
                else
                {
                    int srcSign = op == 1 ? 1 : -1;
                    changed = WeightMirror.MirrorSelf(model, brush.activeBone, mirAxis, srcSign,
                        mirProjection, mirMix, root, out unmatched);
                    what = "'" + model.boneNames[brush.activeBone] + "' " +
                        (srcSign > 0 ? "+" : "-") + "XYZ"[mirAxis].ToString() + " side -> other side";
                }

                model.ApplyToMesh();
                if (heatmap.Enabled) heatmap.Refresh();

                string msg = "Mirrored " + what + ": " + changed + " vert group(s) changed" +
                    " (" + (mirProjection ? "projection, current pose" : "exact, bind pose") +
                    ", mix " + Mathf.RoundToInt(mirMix * 100f) + "%)";
                if (unmatched > 0) msg += "; " + unmatched + " had no mirror match and were left alone";
                SetStatus(msg + ".");
            }
            catch (Exception e)
            {
                Debug.LogError("[WeightStudio] mirror failed: " + e);
                SetStatus("Mirror failed (see log): " + e.Message);
            }
        }
    }
}
