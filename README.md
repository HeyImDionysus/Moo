# 🐄 Moo

Oxide/uMod plugin for Rust. Attaches an invisible, self-powered boombox to the player that plays Doja Cat — Mooo! Everyone nearby hears the music coming from the player's position but can't see the boombox.

## Setup

1. Drop `Moo.cs` into `oxide/plugins/`
2. Add the audio URL to your server's boombox whitelist:
   ```
   BoomBox.ServerUrlList "Mooo,https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3"
   ```
3. Grant permission:
   ```
   oxide.grant user <steamid> moo.use
   ```

## Commands

| Command | Description |
|---------|-------------|
| `/moo` | Attach the invisible boombox and start playing. Toggles play/pause if already attached. |
| `/moo.play` | Resume playback |
| `/moo.stop` | Pause playback (boombox stays attached) |
| `/moo.vol 0-10` | Set volume level (0 = silent, 10 = max) |
| `/moo.stealth` | Toggle stealth mode — when on, only the admin hears it |
| `/moo.remove` | Remove the boombox entirely |

## How It Works

- Spawns a `DeployableBoomBox` parented to the player (hidden inside player model)
- Auto-powers via the `Reserved8` flag — no electrical wiring needed
- Hardcoded to play Doja Cat — Mooo! (`mooo.mp3` hosted in this repo)
- Everyone nearby hears the music by default
- Stealth mode optionally hides the boombox from other players so only the admin hears it
- Survives death and respawn — boombox detaches on death, re-attaches automatically
- Auto-removes when the player disconnects
- Protected from damage, decay, pickup, and looting

## Permission

- `moo.use` — Required to use any `/moo` command

## Files

- `Moo.cs` — The plugin (compilation-tested against Oxide.Rust + Assembly-CSharp refs)
- `mooo.mp3` — Doja Cat - Mooo! audio file (hosted here for streaming)
