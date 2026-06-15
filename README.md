# Sim Inspector

An [Erenshor](https://store.steampowered.com/app/2382520/Erenshor/) mod that lets you inspect any sim player's gear and stats, regardless of zone or proximity.

## Features

- Browse all sim players loaded in the game from a searchable list
- Inspect a sim's equipped items, attributes, derived stats, resistances, and proficiencies
- Click any equipment piece to open the game's native item info window
- Sims in your current zone are highlighted in green
- Resizable, draggable windows
- Configurable hotkey (default: F8)

## Requirements

- [Lunaris Mod Manager](https://github.com/MizukiBelhi/Lunaris/releases)

## Installation

1. Download the latest release DLL
2. Place it in your Erenshor `plugins/SimInspect/` folder
3. Launch the game with Lunaris

Or install directly from the Lunaris vault if available.

## Usage

Press **F8** (configurable in Lunaris settings) to open the Sim Inspector selector window. Type in the search field to filter by name, class, level, or zone. Click a sim to inspect their full equipment and stats.

## Building from Source

Requires the [.NET Framework 4.8 targeting pack](https://dotnet.microsoft.com/download/dotnet-framework/net48) and a local Erenshor installation.

```
dotnet build SimInspect/SimInspect.csproj -c Release -p:ErenshorDir="<path to Erenshor>"
```

The build auto-detects common Steam install paths. You can also set the `ERENSHOR_DIR` environment variable.

The output DLL is automatically deployed to `<Erenshor>/plugins/SimInspect/` when Lunaris is detected.

## License

MIT
