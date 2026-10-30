# Weight Studio Reader (VNyan plugin)

Loads `.vbx` avatars exported by the **Weight Studio** plugin back into VNyan,
reconstructing the full skeleton, meshes, blendshapes and materials, with the
**unlimited bone-influences-per-vertex** data intact (VRM/glTF would have capped it
at 4).

This is the companion to Weight Studio: VBX exists because no standard VTuber format
round-trips more than 4 influences per vertex, so the editor writes VBX and this plugin
reads it.

## Install

The Weight Studio release zip installs both plugins: unzip it, close VNyan and run
`install.bat`. It finds your VNyan folder (or asks you to open it), then puts this plugin
into `<your VNyan folder>\Items\Assemblies\WeightStudioReader`. Start VNyan, open the
**Plugins** window and click **Weight Studio Reader**.

## What it does

- Scans the Weight Studio export folder for `.vbx` files (newest first).
- **Load Selected** rebuilds the avatar in the scene:
  - skeleton transforms with original local TRS + parenting,
  - `SkinnedMeshRenderer` per mesh with `Mesh.SetBoneWeights` (unlimited influences;
    flips `QualitySettings.skinWeights = Unlimited` when any vertex needs more than 4),
  - bindposes, submeshes, blendshape frames,
  - materials: borrowed from your live avatar when one is loaded; otherwise the original
    compiled materials embedded in the `.vbx` when present, with any captured live edits
    (for example from Material Studio) laid on top, or else rebuilt from the captured
    shader properties and textures,
  - a humanoid `Avatar` via `AvatarBuilder.BuildHumanAvatar` when humanoid data is
    present (falls back to Generic if invalid).
- With your avatar loaded in VNyan, the loaded meshes take the place of the avatar's own
  meshes: they ride its bones (so tracking, expressions and blinks keep working) and keep
  its shaders. With no avatar loaded you get a static preview.
- **Remove Last** removes the most recently loaded avatar.
- **Open Folder** opens the export folder in Explorer.
- The **X** at the top-right closes the window, and a green **Update vX** button appears
  next to it when a newer Weight Studio release is out on GitHub (checked at most every
  6 hours).

VBX is stored in Unity's own coordinate space (written straight off live renderers),
so import needs **no axis conversion**, unlike VRM/glTF import.

## Folders scanned

- `Application.persistentDataPath\WeightStudio`   (where the editor writes exports)
- `Application.persistentDataPath\WeightStudioReader`   (drop files here manually)

## Build

Requires Unity **2022.3.22f1** and VNyan installed.

```
build_vnobj.bat ["<your VNyan folder>"]   :: compiles the DLL + builds WeightStudioReader.vnobj -> dist\
```

Without a folder argument it uses the `VNYAN` environment variable, else looks for VNyan
the same way the main installer does, else asks you for the folder. Then run `install.bat`
in the main Weight Studio folder, restart VNyan, open the **Plugins** window and click
**Weight Studio Reader**.

## Source layout

| File | Role |
|------|------|
| `VbxFormat.cs` | VBX data classes + (de)serializer. **Must stay byte-identical to the editor's copy.** |
| `VbxImporter.cs` | Rebuilds the avatar GameObject hierarchy from a `VbxDoc`. |
| `WeightStudioReaderPlugin.cs` | VNyan SDK window plugin (file list + load/remove). |
| `WindowDrag.cs` | Drag-to-move the window. |
| `PluginChrome.cs` | Shared window chrome: the top-right X close button and the GitHub update check ("Update vX" button). Same file in every plugin, only the namespace differs. |
| `_unitybuild/Assets/Editor/WeightStudioReaderBuild.cs` | Builds the UI prefab + `.vnobj`. |

## Keeping VBX in sync

`VbxFormat.cs` is shared between this plugin and the editor. If you change the
format (new fields, version bump), update **both copies** and the `VbxDoc.Version`
constant together, or loading will fail the magic/version check.
