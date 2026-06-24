# Weight Studio Reader (VNyan plugin)

Loads `.vbx` avatars exported by the **Weight Studio** plugin back into VNyan,
reconstructing the full skeleton, meshes, blendshapes, and materials — with the
**unlimited bone-influences-per-vertex** data intact (VRM/glTF would have capped it
at 4).

This is the companion to Weight Studio: VBX exists precisely because no standard
VTuber format round-trips >4 influences/vertex, so the editor writes VBX and this
plugin reads it.

## What it does

- Scans the Weight Studio export folder for `.vbx` files (newest first).
- **Load Selected** rebuilds the avatar in the scene:
  - skeleton transforms with original local TRS + parenting,
  - `SkinnedMeshRenderer` per mesh with `Mesh.SetBoneWeights` (unlimited influences;
    flips `QualitySettings.skinWeights = Unlimited` when any vertex needs >4),
  - bindposes, submeshes, blendshape frames,
  - best-effort materials (`Shader.Find` + PNG textures for base/normal/emissive),
  - a humanoid `Avatar` via `AvatarBuilder.BuildHumanAvatar` when humanoid data is
    present (falls back to Generic if invalid).
- **Remove Last** deletes the most recently loaded avatar.
- **Open Folder** opens the export directory in Explorer.

VBX is stored in Unity's own coordinate space (written straight off live renderers),
so import needs **no axis conversion** — unlike VRM/glTF import.

## Folders scanned

- `Application.persistentDataPath\WeightStudio`   (where the editor writes exports)
- `Application.persistentDataPath\WeightStudioReader`   (drop files here manually)

## Build

Requires Unity **2022.3.22f1** and a VNyan install at `C:\Program Files\VNyan`.

```
build_vnobj.bat   :: compiles the DLL + builds WeightStudioReader.vnobj -> dist\
install_sdk.bat   :: copies dist\ into C:\Program Files\VNyan\Items\Assemblies\WeightStudioReader
```

Restart VNyan, open the **Plugins** window, and click **Weight Studio Reader**.

## Source layout

| File | Role |
|------|------|
| `VbxFormat.cs` | VBX data classes + (de)serializer. **Must stay byte-identical to the editor's copy.** |
| `VbxImporter.cs` | Rebuilds the avatar GameObject hierarchy from a `VbxDoc`. |
| `WeightStudioReaderPlugin.cs` | VNyan SDK window plugin (file list + load/remove). |
| `WindowDrag.cs` | Drag-to-move the window. |
| `_unitybuild/Assets/Editor/WeightStudioReaderBuild.cs` | Builds the UI prefab + `.vnobj`. |

## Keeping VBX in sync

`VbxFormat.cs` is shared between this plugin and the editor. If you change the
format (new fields, version bump), update **both copies** and the `VbxDoc.Version`
constant together, or loading will fail the magic/version check.
