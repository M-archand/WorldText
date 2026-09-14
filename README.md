<a name="readme-top"></a>
<!-- PROJECT LOGO -->
<br />
<div align="center">
  <h1 align="center">World Text</h1>
  <a align="center">A CS2 plugin that allows you to place configurable text on the map. <br>Optionally save placements to a database for multi-server support.</a>
  <br><br>
  <img src="https://github.com/user-attachments/assets/ab482c90-3d9b-4778-bfc8-d26f71e6b544" alt="" style="margin: 0;">
  <img src="https://github.com/user-attachments/assets/98cbcb3c-8192-4cab-9318-21c3717ad4b2" alt="" style="margin: 0;">
</div>

<!-- DEPENDENCIES -->

## Dependencies

To use this plugin, you'll need the following dependencies installed:

- [**CounterStrikeSharp**](https://github.com/roflmuffin/CounterStrikeSharp): CounterStrikeSharp allows you to write server plugins in C# for Counter-Strike 2.
- [**K4-WorldText-API**](https://github.com/M-archand/K4-WorldText-API): This is a shared developer API to handle world text. **Required** - without it every command will reply with `K4-WorldText-API missing.`
- [**CS2MenuManager (optional)**](https://github.com/schwarper/cs2menumanager): This is a shared developer API to handle menus. It's only required if you want to use the move menu command (`!mtext`).

<!-- INSTALLATION -->

## Installation

1. Install the dependencies listed above.
2. Download the latest `WorldText-vX.X.X.zip` from [Releases](https://github.com/M-archand/WallText/releases).
3. Extract the archive into your server's `game/csgo/` folder, so the plugin lands in `addons/counterstrikesharp/plugins/WorldText/`.
4. Start the server, or `css_plugins load WorldText` from the server console.
5. Edit the generated config, then run `!reloadtext` in-game to apply it.

The release archive already contains `Dapper.dll` and `MySqlConnector.dll`, which are only used when `EnableDatabase` is `true`.

<!-- COMMANDS -->

## Commands

All commands require the permission set in `CommandPermission` (default `@css/root`) and must be run **in-game** - they are not available from the server console.

| Command | Configurable | Description |
| --- | --- | --- |
| `!text <group>` | `AddCommand` | Creates the text from the given config group in front of you and saves the placement. E.g. `!text 1` places group 1. Each group can be placed in as many locations as you like. |
| `!rtext` | `RemoveCommand` | Removes the closest placement within **200 units** of you and deletes it from storage. |
| `!mtext` | `MoveCommand` | Opens a menu to adjust the position and angles of the closest placement. Requires [CS2MenuManager](https://github.com/schwarper/cs2menumanager). |
| `!importtext` | no | Imports existing JSON placements into the database. Requires `EnableDatabase: true`. |
| `!reloadtext` | no | Reloads the config and respawns all text in the world. |

<!-- CONFIG -->

## Configuration

- A config file is generated on first use at `addons/counterstrikesharp/configs/plugins/WorldText/WorldText.json`
- When `EnableDatabase` is `false`, placements are stored per map as JSON in `addons/counterstrikesharp/plugins/WorldText/maps/`
- When `EnableDatabase` is `true`, placements are stored in the configured MySQL table instead. Use `!importtext` once to migrate existing JSON placements across.

### Plugin settings

| Key | Default | Description |
| --- | --- | --- |
| `EnableDatabase` | `true` | Store placements in MySQL instead of per-map JSON files. |
| `DatabaseSettings` | - | MySQL connection details. Changes take effect on `!reloadtext`. |
| `AddCommand` | `text` | Command name for placing text, without the `css_` / `!` prefix. |
| `RemoveCommand` | `rtext` | Command name for removing the closest text. |
| `MoveCommand` | `mtext` | Command name for the move menu. |
| `MenuType` | `WasdMenu` | Move menu style: `WasdMenu`, `ChatMenu`, `CenterHtmlMenu`, `ConsoleMenu`, `PlayerMenu`. |
| `MoveDistance` | `5` | Step size in units for each nudge in the move menu. |
| `CommandPermission` | `@css/root` | Permission flag or group required for every command. |

### Text group settings

Each entry under `WorldText` is a numbered group that `!text <group>` places.

| Key | Default | Description |
| --- | --- | --- |
| `bgEnable` | `true` | Draw a background panel behind the text. |
| `bgWidth` | `40` | Width of that background. |
| `textAlignment` | `left` | `left`, `center`, or `right`. Unknown values fall back to `center`. |
| `fontSize` | `24` | Font size. |
| `textScale` | `0.45` | Scale applied to the rendered text. |
| `zOffset` | `0` | Units above the ground for the bottom row of text. |
| `lines` | - | The lines of text, rendered top to bottom. |

### Colors

Prefix a line with `{ColorName}` to color it. The name is any [.NET known color](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.color) - `{Red}`, `{White}`, `{Lime}`, `{Magenta}`, `{Orange}`, `{DeepSkyBlue}`, and so on. Unrecognized names fall back to white. Only a tag at the very start of the line is used, and it colors that whole line.

### Config example

```json
{
  "ConfigVersion": 3,
  "EnableDatabase": true,
  "DatabaseSettings": {
    "host": "",
    "database": "",
    "username": "",
    "password": "",
    "port": 3306,
    "sslmode": "None",
    "table-name": "world_text"
  },
  "RemoveCommand": "rtext",
  "AddCommand": "text",
  "MoveCommand": "mtext",
  "MenuType": "WasdMenu",
  "MoveDistance": 5,
  "CommandPermission": "@css/root",
  "WorldText": {
    "1": {
      "bgEnable": true,
      "bgWidth": 35,
      "textAlignment": "left",
      "fontSize": 24,
      "textScale": 0.45,
      "zOffset": 25,
      "lines": [
        "{Red}First line of text from Group 1.",
        "{White}Second line of text from Group 1.",
        "{Red}Third line of text from Group 1."
      ]
    },
    "2": {
      "bgEnable": true,
      "bgWidth": 70,
      "textAlignment": "center",
      "fontSize": 24,
      "textScale": 0.45,
      "zOffset": 0,
      "lines": [
        "{Lime}First line of text from Group 2.",
        "{Magenta}Second line of text from Group 2.",
        "{White}Third line of text from Group 2."
      ]
    }
  }
}
```

<!-- CREDITS -->

> [!IMPORTANT]
> Credits for the base plugin go to [K4ryuu](https://github.com/K4ryuu)! This plugin is built on top of the logic from his K4-WorldText-API.

<!-- LICENSE -->

## License

Distributed under the GPL-3.0 License. See `LICENSE` for more information.

<p align="right">(<a href="#readme-top">back to top</a>)</p>
