# Weight Studio (VNyan plugin)

A runtime bone-weight editor for VNyan. Paint, smooth, and rebalance skin weights
directly on a loaded avatar — with an **unlimited** number of bone influences per
vertex — then export to **VRM 0.x**, **VRM 1.0**, or **VBX** (a custom format that
preserves >4 influences/vertex, re-loadable via the companion **Weight Studio
Reader** plugin).

## What it does

- **Pick a mesh + active bone** from the loaded avatar.
- **Paint weights** on the model (hold left mouse on the mesh):
  - *Add / Subtract / Replace* — push the active bone's weight up/down/to a target.
  - *Smooth* — average a vertex toward its neighbours.
  - *Blur* — soften the whole influence set locally.
  - Sliders for **radius**, **strength**, **falloff**.
- **Undo / Redo** for every weight edit — brush strokes (one step per stroke),
  Smooth, Limit, and Transfer. Buttons in the window, or **Ctrl+Z** (undo) /
  **Ctrl+Shift+Z** (redo). Keeps the last 24 steps per mesh.
- **Hide / show meshes while painting** — each mesh row has a *shown / hidden*
  toggle that switches that `SkinnedMeshRenderer` on/off, so you can get blocking
  meshes (hair, clothes) out of the way without removing them from the avatar.
- **Boundary-aware Laplacian smoothing** over the whole mesh, or over a brushed
  selection (hold **Shift** while painting to mark a selection, then *Smooth Sel*).
- **Limit influences** — cap to N bones/vertex (renormalized) when you want a
  cheaper or VRM-friendly result.
- **Unlimited influences** are kept internally (`Mesh.SetBoneWeights` +
  `SkinWeights.Unlimited`) until you choose to cap or export.
- **Live heatmap overlay** (Blender weight-paint style): tints the model by the
  active bone's weight (blue = 0 → green = 0.5 → red = 1) and updates as you paint.
  An opacity slider fades it down so the model's real shaders show through.
- **Weight transfer between meshes** (à la Blender's Data Transfer, with more
  control): copy skin weights from one mesh onto another by proximity —
  *Nearest vertex*, *Smooth* (inverse-distance average of N nearby verts), or
  *Nearest surface* (barycentric on the closest triangle). Bones are matched by
  **name**, with blend amount, max-distance, selection-only, and normalize options.
- **Auto-fit colliders**: generate physics colliders that hug the mesh each bone
  controls (pose-independent — vertices are transformed to bone-local space via
  bindposes, then fit with PCA so the result is correct regardless of the current
  pose). Long bones become **capsules**, round ones **spheres**. Tunable radius
  scale, minimum bone weight, minimum vertex count, capsules on/off, skip fingers,
  and humanoid-bones-only. One generation pass feeds every supported physics system:
  - **VRM spring-bone colliders** baked straight into exported `.vrm`
    (VRM 0.x `secondaryAnimation.colliderGroups`; VRM 1.0 `VRMC_springBone`).
  - **VBX** embeds them natively (format v2).
  - **Sidecar JSON** for **JayoPhysBones**, **JayoJigglePhysics**, **VRChat
    PhysBone**, and **DynamicBone** — written next to the export.
- **Export a `.unitypackage`** of Unity-compatible versions of the runtime
  (collider component, spring-bone solver, and a JSON collider importer), targeting
  **Unity 2019.4.x** and **2022.3.x** (UnityEngine + `JsonUtility` only).
- **Tappable tooltips**: every option has a `?` button that explains, in plain
  language, what it does — shown in a tip panel at the bottom of the window.

## Export targets

| Button   | Format   | Influences/vertex | Notes |
|----------|----------|-------------------|-------|
| VRM 0.x  | `.vrm`   | capped at 4       | glTF + `VRM` extension. Top-4 weights renormalized. |
| VRM 1.0  | `.vrm`   | capped at 4       | glTF + `VRMC_vrm` extension. |
| VBX (>4) | `.vbx`   | **unlimited**     | Custom binary. Full skeleton, bindposes, blendshapes, materials. |

> VRM/glTF consumers cap skinning at 4 bones/vertex, so the two VRM exports are
> intentionally lossy on >4-influence verts (strongest 4 kept, renormalized).
> Use **VBX** to keep everything, and load it back with the Reader plugin.

Each export (VRM 0.x, VRM 1.0, VBX, and the `.unitypackage`) opens a native **Save As**
dialog so you choose the file name and location. It defaults to:
`%USERPROFILE%\AppData\LocalLow\<VNyan publisher>\VNyan\WeightStudio\`
(`Application.persistentDataPath\WeightStudio`). The chosen path is printed to the
VNyan log on every export.

### Colliders & .unitypackage

| Button             | Output | Notes |
|--------------------|--------|-------|
| Generate Colliders | (in memory) | Auto-fits one collider per qualifying bone. The status line reports how many were made. |
| Export Sidecars    | `*.physbones.json`, `*.jigglephysics.json`, `*.vrcphysbones.json`, `*.dynamicbone.json` | Drop into the matching physics system. |
| .unitypackage      | `*.unitypackage` | Unity 2019.4 / 2022.3 scripts: collider, spring-bone solver, JSON importer. |

With **Embed colliders in exports** enabled, the generated colliders are also baked
into every subsequent VRM/VBX export.

## Build

Requires Unity **2022.3.22f1** (for the `.vnobj` AssetBundle) and a VNyan install
at `C:\Program Files\VNyan`.

```
build_vnobj.bat      :: compiles the DLL (Unity Roslyn) + builds WeightStudio.vnobj -> dist\
install_sdk.bat      :: copies dist\ into C:\Program Files\VNyan\Items\Assemblies\WeightStudio
```

Then restart VNyan, open the **Plugins** window, and click **Weight Studio**.

`VNyanWeightStudio.csproj` is provided only for IDE IntelliSense — the canonical
build is `build_vnobj.bat`.

## Source layout

| File | Role |
|------|------|
| `WeightModel.cs` | Weld-aware editable skin-weight model (sparse per-vertex dicts). |
| `WeightTools.cs` | Brush ops + boundary-aware Laplacian smoothing + influence limiting. |
| `WeightHeatmap.cs` | Live weight-paint heatmap overlay renderer + adjustable opacity. |
| `WeightTransfer.cs` | Proximity weight transfer between meshes (spatial grid; nearest / smooth / surface). |
| `ColliderGen.cs` | Pose-independent auto-fit of sphere/capsule colliders per bone (bindpose-local point clouds + PCA). |
| `CollidersExport.cs` | Writes collider sidecar JSON for PhysBones / Jiggle / VRChat PhysBone / DynamicBone. |
| `UnityPackageExport.cs` | Emits a `.unitypackage` (gzip USTAR tar) with Unity 2019.4/2022.3 runtime scripts. |
| `VbxFormat.cs` | VBX data classes + binary (de)serializer (v2 adds colliders). **Shared verbatim with the Reader.** |
| `VbxExporter.cs` | Reads live mesh weights (and colliders) and writes `.vbx`. |
| `GltfVrmExporter.cs` | Self-contained glTF/GLB writer + VRM 0.x / 1.0 extensions (incl. spring-bone colliders). |
| `BoneWeightStudioPlugin.cs` | The VNyan SDK window plugin (UI wiring, paint loop, export). |
| `WindowDrag.cs` | Drag-to-move the window. |
| `_unitybuild/Assets/Editor/WeightStudioBuild.cs` | Builds the UI prefab + `.vnobj`. |

## Notes / limitations

- The mesh must be **readable** to edit/export it. Most avatar meshes are.
- Painting raycasts against a baked copy of the active mesh; if no scene camera is
  tagged `MainCamera`, the first active camera is used.
- VRM export writes geometry, skinning, materials (base color / normal / emissive),
  the humanoid bone map, and (when colliders are generated + embedding is on)
  spring-bone **colliders**. It does not (yet) author spring-bone *chains*, look-at,
  or expression/blendshape-proxy metadata.
- VRM 0.x collider groups are spheres only, so generated **capsules are approximated
  by three spheres** (start / middle / end) in that one target; every other target
  (VRM 1.0, VBX, DynamicBone, VRChat PhysBone, JayoPhysBones/Jiggle) keeps the real
  capsule.
