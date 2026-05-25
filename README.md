# S2MM (Subnautica 2 Mod Manager)

S2MM is a standalone Windows mod manager for **Subnautica 2**.

## Key Features
- Drag-and-drop install for `.zip`, `.7z`, and folder mods
- Auto-detect Subnautica 2 install path
- One-click apply/purge workflow
- UE4SS + SN2 Mod Settings category handling
- Nexus metadata integration (title/author/icon/version/description where available)
- Right-click tools: pin, rename, remove, set category, link to Nexus URL
- NXM protocol support (`Add to Mod Manager` links)
- Full session logging to `_filestructure/logs`

## Quick Start
1. Run `S2MM.exe`
2. Set/confirm Subnautica path if prompted
3. Drop mods into the left panel
4. Click **Apply Mods**

## Folder Layout
- `_filestructure/mods` - manager-staged mod folders
- `_filestructure/config.json` - app config
- `_filestructure/modlist.json` - installed/pinned/category/link data
- `_filestructure/logs` - runtime logs

## Notes
- S2MM supports both pak-style and folder/UE4SS-style mod payloads.
- If a Nexus download cannot be resolved directly, S2MM will open the mod files page.

## Links
- Nexus: https://www.nexusmods.com/subnautica2/mods/268
- GitHub: https://github.com/Tj-Ace/S2MM
