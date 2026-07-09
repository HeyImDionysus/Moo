# 🐄 Moo

Oxide/uMod plugin for Rust. Attaches an invisible, self-powered boombox to the player that plays Doja Cat — Mooo! Everyone nearby hears it.

## Setup

1. Drop `Moo.cs` into `oxide/plugins/`
2. Add the audio URL to your server's boombox whitelist:
   ```
   BoomBox.ServerUrlList "Mooo,https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3"
   ```
3. Grant permission: `oxide.grant user <steamid> moo.use`

## Commands

| Command | Description |
|---------|-------------|
| `/moo` | Toggle the invisible boombox on/off |
| `/moo.remove` | Remove the boombox |

## How It Works

- Spawns a `DeployableBoomBox` parented to the player (hidden inside player model)
- Auto-powers via `Reserved8` flag — no wiring needed
- Everyone nearby hears the music
- Survives death/respawn, auto-removes on disconnect
- Protected from damage, decay, pickup, and looting

## Permission

- `moo.use` — Required to use `/moo`
