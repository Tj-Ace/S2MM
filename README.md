# S2MM - Subnautica 2 Mod Manager

S2MM is a standalone Windows mod manager for **Subnautica 2** focused on easy drag-and-drop install, category organization, and one-click deployment.

## What It Does

- Drag-and-drop install for:
  - `.zip`
  - `.7z`
  - mod folders
- Auto-detects Subnautica 2 install path and stores it in config
- Auto-deploys installed mods to game when game path is detected
- Distinguishes deployment types:
  - `.pak/.utoc/.ucas` -> `Content\\Paks\\~mods`
  - UE4SS/folder mods -> `Binaries\\Win64\\ue4ss\\Mods` or relevant mod folder locations
- Pulls Nexus metadata (when available) using your API key
- Logs all actions to `_filestructure\\logs`
- Category management with grouping + pinning
- Right-click actions for mod/category management

## Requirements

- Windows
- Subnautica 2
- Optional for richer metadata: Nexus Mods API key
- Optional for `.7z` extraction:
  - 7-Zip / NanaZip / `7z.exe`/`7zz.exe`
  - or WinRAR (fallback supported)

## File Layout

S2MM expects this structure:

- `S2MM.exe`
- `_filestructure\\`
  - `S2MM.cs`
  - `config.json`
  - `modlist.json`
  - `mods\\`
  - `logs\\`
  - `assets\\`

## First Run

1. Launch `S2MM.exe`.
2. Confirm Subnautica path detection in the status/log area.
3. If needed, use in-app path actions to set/open game folders.
4. Add mods by dragging archives/folders into the mod list.
5. Click **Apply Mods**.

## Config Files

- `config.json`
  - app settings such as Subnautica path, Nexus key, version
- `modlist.json`
  - installed mods, notes, categories, pinned states, links

## Safety / Cleanup

- **Purge All** removes all traces of mods installed by S2MM.
- `logs` folder records actions and errors for troubleshooting.

## Troubleshooting

- `.7z` fails to import:
  - Ensure 7-Zip/NanaZip is installed OR WinRAR is installed.
  - Check latest log in `_filestructure\\logs`.
- Mod not showing in game:
  - Verify detected game path is correct.
  - Re-run **Apply Mods**.
  - Check whether mod is a pak-based mod or folder/UE4SS mod.

## Notes

- S2MM is an early-development tool and may evolve quickly.
- Back up saves before major mod changes.
