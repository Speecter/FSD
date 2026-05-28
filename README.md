# FSD Supercruise

**FSD Supercruise** is a Space Engineers mod that adds a custom fast-travel system inspired by Elite Dangerous. It introduces new Fusion Superdrive (FSD) upgrade modules and warp mechanics to make large-scale travel smoother, more immersive, and configurable.

## What this project does

- Adds four new FSD upgrade modules:
  - `FSDriveSmall`
  - `FSDriveLarge`
  - `PrototechFSDriveSmall`
  - `PrototechFSDriveLarge`
- Provides a new warp/jump system with charging, heat, and power management.
- Includes custom audio, particle, and model assets for a polished supercruise experience.
- Supports configurable behavior via `FSDriveConfig.cfg` in world storage.

## Why this project is useful

This mod is useful for players and builders who want:

- a more dynamic travel system beyond standard Space Engineers thrusters and jump drives
- large/small FSD blocks with different power, speed, and performance profiles
- Prototech variants for faster jumps and alternate tuning
- audio cues for charge, jump entry/exit, and supercruise gravity effects
- customizable restrictions for gravity, enemy detection, heat, and jump delay

## Quick start

### Install locally

1. Clone or extract this repository.
2. Copy the mod folder into your Space Engineers mods directory:
   - `%appdata%\SpaceEngineers\Mods\Elite Dangerous FSD Supercruise`
   - or the corresponding game mods folder for your installation.
3. Start Space Engineers.
4. Enable the mod in world settings or in the world workshop/mod list.
5. Launch a world and place one of the new FSD blocks from the upgrade module category.

### Use the mod

1. Build `FSDriveSmall`, `FSDriveLarge`, `PrototechFSDriveSmall`, or `PrototechFSDriveLarge`.
2. Provide sufficient power and enable the block.
3. Allow the drive to charge and monitor the custom info panel.
4. Use the block as part of a ship to enter supercruise and jump smoothly across space.

## Configuration

- `FSDriveConfig.cfg` is stored in world storage and is created/loaded automatically.
- You can tune:
  - max speed and start speed
  - heat limits and dissipation
  - power requirements and scaling
  - gravity activation settings
  - enemy proximity jump delay
  - Prototech jump timing

> Note: configuration values are validated on load and will be corrected automatically if out of range.

## Project structure

- `Data/` - Space Engineers configuration files and game data definitions
- `Data/Scripts/WarpDrive/` - main mod logic written in C#
- `Audio/SC/` - custom sound effects for charging, jump, glide, and gravity
- `Models/` - drive and construction models for small and large FSD blocks
- `Textures/` - GUI icons used by the blocks
- `metadata.mod` - mod metadata and version information
- `modinfo.sbmi` - Steam Workshop metadata and workshop ID

## Support

- If you installed the mod from Steam Workshop, use the workshop page comments to report issues.
- For repository-based support, use the GitHub issue tracker on the repository where this mod is hosted.
- Refer to `LICENSE` for licensing details.

## Maintainers and contributions

- Maintained by the mod author and contributors working on this repository.
- Contributions are welcome via pull requests and issue reports.
- For major changes, open an issue first so the maintainer can review the proposed direction.

## Metadata

- Mod version: `1.0`
- Steam Workshop ID: `2640137506`
- License: see [`LICENSE`](LICENSE)

