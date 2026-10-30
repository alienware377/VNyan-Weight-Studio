# VNyan Weight Studio: Bone Weight Painter for VTuber Avatars

A runtime **bone weight editor** for [VNyan](https://suvidriel.itch.io/vnyan). Paint,
smooth, mirror and rebalance skin weights directly on a loaded avatar, with an
**unlimited** number of bone influences per vertex, then export to **VRM 0.x**,
**VRM 1.0**, or **VBX** (a custom format that keeps more than 4 influences per vertex and
loads back into VNyan with the companion **Weight Studio Reader** plugin).

Made for VTubers who want to fix or tweak avatar skinning without leaving VNyan or
reopening Blender or Unity. Works with VSeeFace-compatible `.vsfavatar` avatars and
standard VRM models.

One download, two plugins:

| Plugin | What it is for |
|---|---|
| **Weight Studio** | Paint, smooth, mirror and transfer weights; fit, edit and export physics colliders; export VRM / VBX / `.unitypackage`. |
| **Weight Studio Reader** | Loads a `.vbx` back into VNyan with all its bone influences intact. |

## What's new in v1.1.0

- **Collider editor window.** Click **Edit Colliders** to pick any collider from a list or
  straight on the model, then move, resize, rotate, switch sphere/capsule, duplicate,
  delete or add one on the active bone. Undo/Redo keeps 50 steps.
- **Show Colliders.** Wireframes of every collider drawn over the model (spheres cyan,
  capsules yellow, the one you are editing magenta).
- **Import Colliders.** Load colliders back from any collider config Weight Studio
  exported (PhysBones, Jiggle, VRChat PhysBones or DynamicBone JSON). Same names are
  replaced, new ones are added. **Export Colliders** now asks where to save.
- **Mirror weights (L <-> R).** Copy the active bone's weights onto its left/right partner,
  or mirror one side of a bone onto its other side. Pick the axis, an exact match in the
  model's default pose or a projection onto the current pose, and how much to mix in.
- **Foldable sections.** Click a section title (`[+]` / `[-]`) to fold it away. Meshes and
  Bones start open.
- **Heatmap follows shape keys.** The weight colours now sit on the shape you actually see,
  even when shape keys reshape the body or an outfit.
- **Brush and Weight Transfer use live shape keys** too, so strokes land where you aim and
  transfers sample the visible surface.
- **Plugin conflict warning.** A note appears under the window when a soft-body or weight
  plugin (such as Soft Body or Bone Weight Smoother) is changing the avatar while you paint.
  **Got it** hides it until something changes.
- **Hidden meshes stay hidden.** The shown/hidden buttons are remembered across restarts
  and avatar reloads, and **Save Settings** saves sliders, toggles and that state in one go.
- **Material edits carried into `.vbx`.** Exporting straight from a `.vsfavatar` file
  (**Ctrl+Shift+E**) applies your saved Material Studio edits when the Material Studio
  plugin is installed, and the Reader lays captured material values over
  the original compiled materials when it loads a `.vbx` on its own.
- **X close button and update button** in both windows (see
  [Updates](#updates-the-update-vx-button)).
- Collider **Radius scale** now goes down to 0.05 for very tight fits.

## Features

- **Pick a mesh and an active bone** from the loaded avatar.
- **Paint weights** on the model (turn on Paint mode, hold the left mouse button on the mesh):
  - *Add / Sub / Replace*: push the active bone's weight up, down or toward a target.
  - *Smooth*: average a vertex toward its neighbours.
  - *Blur*: soften the whole influence set locally.
  - Sliders for **radius**, **strength** and **falloff**.
- **Undo / Redo** for every weight edit: brush strokes (one step per stroke), Smooth,
  Limit, Mirror and Transfer. Buttons in the window, or **Ctrl+Z** (undo) /
  **Ctrl+Shift+Z** (redo). Keeps the last 24 steps per mesh.
- **Hide / show meshes while painting**: each mesh row has a *shown / hidden* toggle that
  switches that `SkinnedMeshRenderer` on or off, so hair or clothes can get out of the way
  without being removed from the avatar. The choice is remembered.
- **Boundary-aware Laplacian smoothing** over the whole mesh, over a brushed selection
  (hold **Shift** while painting to mark a selection, then *Smooth Sel*), or for the active
  bone only (*Smooth Bone*, *Smooth Bone Sel*).
- **Limit influences**: cap to N bones per vertex (renormalized) when you want a cheaper
  or VRM-friendly result.
- **Unlimited influences** are kept internally (`Mesh.SetBoneWeights` +
  `SkinWeights.Unlimited`) until you choose to cap or export.
- **Live heatmap overlay** (Blender weight-paint style): tints the model by the active
  bone's weight (blue = 0, green = 0.5, red = 1) and updates as you paint. An opacity
  slider fades it so the model's real shaders show through.
- **Mirror weights** across X, Y or Z: active bone to its L/R partner (names like `_L`/`_R`,
  `.L`/`.R`, `Left`/`Right`, `L_`/`R_`), or one side of a bone onto its other side.
- **Weight transfer between meshes** (like Blender's Data Transfer, with more control):
  copy skin weights from one mesh onto another by proximity with *Nearest* (closest
  vertex), *Smooth* (inverse-distance average of N nearby verts) or *Surface* (barycentric
  on the closest triangle). Bones are matched by **name** (or by order, for identical
  skeletons), with blend amount, max distance, selection-only and normalize options.
- **Auto-fit colliders**: generate physics colliders that hug the mesh each bone controls.
  The fit is pose-independent (vertices are moved to bone-local space via bindposes, then
  fitted with PCA), so the result is right whatever the current pose. Long bones become
  **capsules**, round ones **spheres**. Tunable radius scale, minimum bone weight, minimum
  vertex count, capsules on/off, skip fingers, and humanoid bones only. One generation pass
  feeds every supported physics system:
  - **VRM spring-bone colliders** baked straight into exported `.vrm`
    (VRM 0.x `secondaryAnimation.colliderGroups`; VRM 1.0 `VRMC_springBone`).
  - **VBX** embeds them natively.
  - **JSON configs** for **JayoPhysBones**, **JayoJigglePhysics**, **VRChat PhysBone** and
    **DynamicBone**, which can also be imported back.
- **Collider editor and viewer** (see What's new above).
- **Export a `.unitypackage`** of Unity-compatible versions of the runtime (collider
  component, spring-bone solver, a JSON collider importer and a `.vbx` importer), targeting
  **Unity 2019.4.x** and **2022.3.x**.
- **Tappable tooltips**: every option has a `?` button that explains, in plain language,
  what it does, shown in a tip panel at the bottom of the window.

## Requirements

- Windows and [VNyan](https://suvidriel.itch.io/vnyan), with **Settings > Misc > Allow
  Third Party Plugins** turned on.
- The avatar's meshes must be **readable** to edit or export them. Most avatar meshes are.
- Optional: the Material Studio plugin, if you want its material edits carried into `.vbx`
  files exported with Ctrl+Shift+E.

## Install

1. Download **`VNyan-Weight-Studio-v1.1.0.zip`** from
   [Releases](https://github.com/alienware377/VNyan-Weight-Studio/releases) and unzip it.
2. Close VNyan and run **`install.bat`**. It looks for VNyan by itself and asks you to
   confirm, or asks you to **open your VNyan folder** (the one with `VNyan.exe` inside).
   It only asks for admin rights if that folder is write-protected, and it installs both
   plugins.
3. Start VNyan, open the **Plugins** window and click **Weight Studio** (or
   **Weight Studio Reader**).

Prefer doing it by hand? Copy the `WeightStudio` and `WeightStudioReader` folders from the
zip into `<your VNyan folder>\Items\Assemblies\`.

## Usage

### Painting

1. Load your avatar in VNyan, open **Weight Studio**, pick a mesh in **Meshes** and a bone
   in **Bones** (the active bone is what the brush paints).
2. Tick **Paint mode**, choose a brush mode and drag on the model with the left mouse
   button. Hold **Shift** to paint a selection instead.
3. Turn on the **Heatmap overlay** to see the active bone's weights as colours.
4. Use **Smooth Mesh**, **Smooth Sel**, **Smooth Bone** or **Limit Inf** to clean up, and
   **Undo / Redo** whenever you like.

### Mirror weights

Pick a bone, then:

- **Mirror Active Bone -> Opposite (L/R)** copies its weights onto its partner on the
  other side.
- **Self + -> -** and **Self - -> +** copy one side of the bone onto its own other side.
- **Axis** switches between X (the usual left/right), Y and Z, and **Mix** sets how much of
  the mirrored value replaces what is there.
- With **Project on current pose** off, vertices are matched exactly in the model's default
  (bind) pose. With it on, they are matched on the current pose instead: put the model in a
  still, symmetric pose with nothing clipping together and no live tracking. Weight Studio
  checks that nothing is moving first and tells you if it is.

### Weight transfer

Select the mesh to copy **from** and click **Set Source**, select the mesh to copy **into**,
pick a mapping (Nearest, Smooth or Surface) and click **Transfer Weights**.

### Colliders

1. In **Colliders**, set the options and click **Generate Colliders**.
2. **Show Colliders** draws them on the model. **Edit Colliders** opens the editor: pick one
   from the list or tick **Pick in 3D view** and click it on the model, then use the sliders
   (radius, length, offset and rotation). While the editor is open, Ctrl+Z undoes collider
   edits and Ctrl+Shift+Z or Ctrl+Y redoes them.
3. **Export Colliders** writes the four JSON configs, **Import Colliders** reads any of them
   back, and **Embed colliders in exports** bakes them into your VRM and VBX exports.

### Exporting

Use the **VRM 0.x**, **VRM 1.0** or **VBX (>4)** buttons in **Export**. Each one opens a
**Save As** dialog. **Ctrl+Shift+E** (with the window open) builds a `.vbx` straight from a
`.vsfavatar` file on disk, without loading it as your avatar.

### Weight Studio Reader

Open **Weight Studio Reader**, pick a `.vbx` from the list and click **Load Selected**.

- With your avatar loaded in VNyan, the `.vbx` meshes take the place of the avatar's own
  meshes, ride its bones (so tracking, expressions and blinks keep working) and keep its
  shaders.
- With no avatar loaded, the `.vbx` shows as a static preview, using the original compiled
  materials stored in the file when there are any (with captured material edits on top).
- **Remove Last** takes the last loaded `.vbx` away again, **Open Folder** opens the folder
  it reads from.

It lists `.vbx` files from `WeightStudio` (where Weight Studio saves exports) and
`WeightStudioReader` (drop your own files there), both inside VNyan's data folder
(`%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan`).

### Keyboard shortcuts (while the window is open)

| Keys | What it does |
|---|---|
| Ctrl+Z / Ctrl+Shift+Z | Undo / redo weight edits. While the Collider Editor is open: collider edits instead (Ctrl+Y also redoes) |
| Shift while painting | Paint a selection instead of weights |
| Ctrl+Shift+E | Export a `.vbx` straight from a `.vsfavatar` file |

## Updates: the "Update vX" button

Both plugins check this repository's GitHub releases at most every 6 hours (the answer is
remembered between launches). When a newer release is out, a green **Update vX** button
appears at the top of the window, next to the X. Clicking it opens the release page so you
can download it. The check never blocks the plugin and stays quiet when you are offline.

## Export targets

| Button   | Format   | Influences/vertex | Notes |
|----------|----------|-------------------|-------|
| VRM 0.x  | `.vrm`   | capped at 4       | glTF + `VRM` extension. Top-4 weights renormalized. |
| VRM 1.0  | `.vrm`   | capped at 4       | glTF + `VRMC_vrm` extension. |
| VBX (>4) | `.vbx`   | **unlimited**     | Custom binary. Full skeleton, bindposes, blendshapes, materials. |

> VRM/glTF consumers cap skinning at 4 bones per vertex, so the two VRM exports are
> intentionally lossy on vertices with more than 4 influences (the strongest 4 are kept and
> renormalized). Use **VBX** to keep everything, and load it back with the Reader plugin.

Each export (VRM 0.x, VRM 1.0, VBX and the `.unitypackage`) opens a native **Save As**
dialog so you choose the file name and location. It starts in
`%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\WeightStudio\`
(`Application.persistentDataPath\WeightStudio`). The chosen path is printed to the VNyan
log on every export.

### Colliders and .unitypackage

| Button             | Output | Notes |
|--------------------|--------|-------|
| Generate Colliders | (in memory) | Auto-fits one collider per qualifying bone. The status line reports how many were made. |
| Export Colliders   | `*.physbones.json`, `*.jigglephysics.json`, `*.vrcphysbones.json`, `*.dynamicbone.json` | You pick a base name; drop each file into the matching physics system. |
| Import Colliders   | (in memory) | Reads any of the four files back. Same names are replaced, new ones added. |
| .unitypackage      | `*.unitypackage` | Unity 2019.4 / 2022.3 scripts: collider, spring-bone solver, JSON importer, `.vbx` importer. |

With **Embed colliders in exports** turned on, the colliders are also baked into every
VRM/VBX export after that.

## Notes and limitations

- Painting raycasts against a baked copy of the active mesh; if no scene camera is tagged
  `MainCamera`, the first active camera is used.
- Turn off soft-body and weight plugins (such as Soft Body or Bone Weight Smoother) while
  you paint. They reshape or reweight the avatar underneath Weight Studio, and the warning
  note tells you when one is active.
- VRM export writes geometry, skinning, materials (base color / normal / emissive), the
  humanoid bone map and (when colliders are generated and embedding is on) spring-bone
  **colliders**. It does not (yet) author spring-bone *chains*, look-at, or
  expression/blendshape-proxy metadata.
- VRM 0.x collider groups are spheres only, so **capsules are approximated by three
  spheres** (start, middle, end) in that one target; every other target (VRM 1.0, VBX,
  DynamicBone, VRChat PhysBone, JayoPhysBones/Jiggle) keeps the real capsule.

## Build from source

You need **Unity 2022.3.22f1** (it builds the `.vnobj` window bundles, and its Roslyn
compiler builds the DLLs) and VNyan installed (the DLLs are compiled against the files in
its `VNyan_Data\Managed` folder).

```
build_vnobj.bat ["<your VNyan folder>"]          :: Weight Studio        -> dist\
Reader\build_vnobj.bat ["<your VNyan folder>"]   :: Weight Studio Reader -> Reader\dist\
install.bat                                      :: installs both from dist\ and Reader\dist\
python make_release_zip.py                       :: builds the release zip in release\
```

Without a folder argument the build scripts use the `VNYAN` environment variable, else
look for VNyan the same way the installer does, else ask you for the folder. Set `UNITY` to
a `Unity.exe` path to use a different editor. The `.csproj` files are only for IDE
IntelliSense; point their reference paths at your own VNyan folder.

## Source layout

| File | Role |
|------|------|
| `WeightModel.cs` | Weld-aware editable skin-weight model (sparse per-vertex dicts). |
| `WeightTools.cs` | Brush ops + boundary-aware Laplacian smoothing + influence limiting. |
| `WeightHeatmap.cs` | Live weight-paint heatmap overlay renderer + adjustable opacity. |
| `WeightTransfer.cs` | Proximity weight transfer between meshes (spatial grid; nearest / smooth / surface). |
| `WeightMirror.cs` | Mirror maths: L/R bone pairing, exact (bind pose) and projection (current pose) matching. |
| `MirrorPanel.cs` | Mirror Weights controls, including the hold-still check before a projection mirror. |
| `LiveShapeKeys.cs` | Captures shape-key weights as VNyan renders them, for the heatmap, brush and transfer. |
| `ColliderGen.cs` | Pose-independent auto-fit of sphere/capsule colliders per bone (bindpose-local point clouds + PCA). |
| `ColliderEditorPanel.cs` | The Collider Editor window: list, 3D picking, sliders, add/duplicate/delete, undo/redo. |
| `ColliderImport.cs` | Reads colliders back from any of the exported JSON configs. |
| `CollidersExport.cs` | Writes collider JSON for PhysBones / Jiggle / VRChat PhysBone / DynamicBone. |
| `UnityPackageExport.cs` | Emits a `.unitypackage` (gzip USTAR tar) with Unity 2019.4/2022.3 runtime scripts. |
| `VbxFormat.cs` | VBX data classes + binary (de)serializer. **Shared verbatim with the Reader.** |
| `VbxExporter.cs` | Reads live mesh weights (and colliders) and writes `.vbx`. |
| `VsfAvatarReader.cs` | Loads an avatar straight from a `.vsfavatar` file for the standalone VBX export. |
| `GltfVrmExporter.cs` | Self-contained glTF/GLB writer + VRM 0.x / 1.0 extensions (incl. spring-bone colliders). |
| `BoneWeightStudioPlugin.cs` | The VNyan SDK window plugin (UI wiring, paint loop, export, collider viewer). |
| `SectionsPanel.cs` | Foldable window sections. |
| `PluginConflicts.cs` | The note shown when another plugin is changing the avatar. |
| `DebugProbe.cs` | Developer self-test. Idle unless a `WeightStudio_probe.txt` trigger file appears in VNyan's data folder. |
| `NativeFileDialog.cs` | Windows Open / Save As dialogs. |
| `WindowDrag.cs` | Drag-to-move the window. |
| `PluginChrome.cs` | Shared window chrome: the top-right X close button and the GitHub update check ("Update vX" button). Same file in every plugin, only the namespace differs. |
| `_unitybuild/Assets/Editor/WeightStudioBuild.cs` | Builds the UI prefabs + `.vnobj`. |
| `Reader/` | Weight Studio Reader: its own sources, build script and `dist\` (see [Reader/README.md](Reader/README.md)). |
| `install.bat`, `install.ps1` | The installer for both plugins. |
| `make_release_zip.py` | Builds the release zip. |

## License

MIT. See [LICENSE](LICENSE).
