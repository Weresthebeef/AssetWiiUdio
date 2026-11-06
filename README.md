# AssetWiiUdio

A Windows desktop explorer for **Wii U NUS packages**, with specialized inspection tools for **Disney Infinity assets**.

AssetWiiUdio lets you browse a package's virtual filesystem, inspect metadata, export selected files, and open supported Disney Infinity archives without extracting the entire game first. Additional research tools provide texture previews, material trees, scene inspection, and experimental static mesh/rig previews.

Built with **C# / .NET 8 WPF**. This is an unofficial research tool; support varies by package, archive variant, shader layout, and asset type.

## Features

- **Package and filesystem views:** browse NUS contents and the decrypted virtual FST.
- **Search and filtering:** find virtual files by hash, filename, or candidate mapped name.
- **Selective export:** export selected virtual files without requiring a full-game extraction.
- **Diagnostics:** inspect package completeness, FST offsets, hashed-content handling, and selected-file metadata.
- **Disney Infinity archive browser:** browse supported proprietary containers in a hierarchical tree and export members.
- **Texture tools:** preview supported DDS/GX2 textures and export supported images as PNG or DDS.
- **Material inspection:** view MTB sections and texture references in a tree.
- **Scene inspection:** examine supported OCT BinaryTup structure and scene data.
- **Experimental mesh preview:** display supported geometry, material textures, rig/scene nodes, wireframes, and bind-pose rotation tests.
- **External asset lookup:** use another game/assets folder to resolve supported texture dependencies.
- **Optional character pedestal:** preview a character centered above the bundled base.

These inspection features are not a full recreation of the game's renderer, animation engine, or physics.

## Requirements

- Windows.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source.
- Your local Wii U package or extracted asset files.
- Appropriate locally supplied keys for encrypted package access. Nintendo common keys and title-key databases are not included.

The project uses WPF, with Windows Forms for selected dialogs. No external NuGet packages are declared in the project.

## Build and run

Open a terminal in the directory containing `AssetWiiUdio.csproj`:

```powershell
dotnet build -c Release
dotnet run -c Release
```

You can also open the project in Visual Studio with the .NET desktop development workload installed. Opening the `.csproj` avoids requiring IDE support for the included `.slnx` solution format.

To publish a self-contained 64-bit Windows application:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Keep `asset_names.tsv` beside the published application; the project copies it into the output directory. `DIBASE.json` is embedded during the build.

## Getting started

1. Choose **Open NUS Folder** and select the directory containing the package's TMD and content files.
2. Use **Settings → Configure Common Key** if ticket-based key resolution needs a local common-key file, or supply the requested title key when opening the virtual filesystem.
3. Choose **Open Virtual Filesystem** and browse the tree.
4. Select a file to inspect its details or export it using **Export Selected Files**.
5. For supported Disney Infinity containers, open **Archive Browser**, then inspect or export their members.

Already extracted files can be explored through **Open Extracted Folder**. Mesh previews may need their matching vertex/index buffers, material bundles, and textures. A mesh alone is not necessarily a complete character package.

## Names and hashed paths

`asset_names.tsv` provides candidate names for hashed asset paths, based on reference mapping research. A matching label is a lookup aid, not independent proof that two platforms contain identical assets. Preserve the original hashed path when sharing diagnostics.

## Keys and local settings

The application supports locally supplied key material and ticket-based title-key resolution. Local key-file selection/settings and external preview asset-folder preferences may persist under:

```text
%LOCALAPPDATA%\AssetWiiUdio\
```

Do not add personal key files, tickets, title-key databases, game content, or local settings to a public repository. The source contains DI-specific archive encryption profiles used by its format reader; these are separate from Nintendo package keys.

## Optional full extraction with CDecrypt

**Decrypt Game (CDecrypt)** uses a separate `cdecrypt.exe` placed beside `AssetWiiUdio.exe`. Build that executable separately from the included source, or use a trusted compatible build. AssetWiiUdio does not automatically compile it.

The bundled source and its license are preserved under `ThirdParty/cdecrypt-4.8/`. See that component's README and license for its build requirements and terms. The virtual filesystem reader does not require launching CDecrypt.

## Current limitations

- Compatibility is not guaranteed for every Wii U title, update layout, archive key, or compression method.
- Candidate hashed-name mappings can be incomplete or ambiguous.
- Texture preview supports selected formats and layouts; unsupported GX2 tiling, streaming data, or shaders may still require additional decoding.
- Mesh, skin-weight, eye, and scene handling are research features. Unsupported layouts may be rejected or render incompletely.
- WPF preview shading does not reproduce all native materials, tangent-space effects, animation layers, or cloth behavior.
- Rig rotation controls are diagnostic tests, not proof of complete native animation playback.
- Source packages are read for inspection/export; this tool does not provide a complete Wii U-to-PC conversion or game archive repacking pipeline.

## Source layout

| Area | Main files |
|---|---|
| Interface and browsing | `MainWindow.xaml`, `MainWindow.xaml.cs`, `ArchiveBrowserWindow.cs` |
| Package filesystem | `TitleManifest.cs`, `VirtualFst.cs`, `VirtualContentReader.cs` |
| Key resolution | `TicketKeyResolver.cs`, `TitleKeyDatabase.cs` |
| Diagnostics | `FstDiagnostics.cs`, `HashedContentDiagnostics.cs`, `NusPackageCheck.cs` |
| DI archives and names | `DisneyInfinityArchive.cs`, `DisneyInfinityAsset.cs`, `AssetNameResolver.cs` |
| Texture/material inspection | `Gx2ExternalPreview.cs`, `MaterialBundleReader.cs`, `MtbTextureTable.cs` |
| Mesh and rig preview | `DI3MeshReader.cs`, `Di3MeshPreviewWindow.cs`, `PreviewRig.cs`, `NativeVertexData.cs` |
| Scene and eye research | `WiiUOctInspector.cs`, `Di3SceneInspector.cs`, `DI3SourceEyeDecoder.cs` |

Historical version notes are retained as development history. They may describe features or limitations that have since changed; current source behavior takes precedence.

## Contributing and reporting issues

Useful contributions include verified format layouts, additional archive/texture support, diagnostic improvements, and reproducible parser fixes.

When reporting an issue, include the application revision, platform/game/update version, original hashed path, candidate name, operation attempted, and exact error. Selected-file and FST diagnostics help distinguish package extraction problems from inner-archive problems. Avoid sharing keys or game assets; metadata and synthetic reproductions are preferable.

## Credits

AssetWiiUdio builds on community research into Wii U packages and Disney Infinity formats. Preserve existing source attribution and the bundled CDecrypt license when redistributing components. The embedded preview pedestal was supplied for this project's base-preview feature; it is a display resource, not required game content.

Disney Infinity, Nintendo, and related names/assets belong to their respective rights holders. This is an unofficial project and is not affiliated with or endorsed by Nintendo, Disney, or Avalanche Software.

## Source-package verification

The staged GitHub source package was built successfully in Release configuration on November 5, 2026, with .NET SDK 10.0.403 targeting net8.0-windows: zero errors. Existing nullable-reference warnings remain, and the offline NuGet vulnerability lookup emitted NU1900. This verifies compilation, not full package/gameplay compatibility.
