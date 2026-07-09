# 🐄 Moo

An Oxide/uMod plugin for Rust that attaches an invisible, self-powered boombox to an admin player. Nearby players hear music coming from the admin's position but can't see the boombox. Built to play Doja Cat — Mooo! but works with any audio URL.

## Setup

1. Drop `Moo.cs` into your `oxide/plugins/` folder
2. Add the audio URL to your server's boombox whitelist:
   ```
   BoomBox.ServerUrlList "Mooo,https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3"
   ```
3. Grant permission to admins:
   ```
   oxide.grant user <steamid> moo.use
   ```

## Commands

| Command | Description |
|---------|-------------|
| `/moo` | Attach the invisible boombox and start playing. If already attached, toggles play/pause. |
| `/moo.play` | Resume playback |
| `/moo.stop` | Pause playback (boombox stays attached) |
| `/moo.vol 0-10` | Set volume level (0 = silent, 10 = max) |
| `/moo.stealth` | Toggle stealth mode — when on, only the admin hears it |
| `/moo.remove` | Remove the boombox entirely |

## How It Works

- Spawns a `DeployableBoomBox` entity parented to the player at a small vertical offset (hidden inside the player model)
- Auto-powers the boombox via the `Reserved8` flag — no electrical wiring needed
- Networked to all nearby players by default so everyone hears the music
- Stealth mode optionally hides the entity from networking so only the admin hears it
- Handles player death and respawn — boombox detaches on death, re-attaches automatically on respawn
- Auto-removes when the player disconnects
- Protected from damage, decay, pickup, and looting

## Configuration

After first load, edit `oxide/config/Moo.json`:

```json
{
  "Audio URL (direct .mp3 link)": "https://raw.githubusercontent.com/HeyImDionysus/Moo/main/mooo.mp3",
  "Default Volume (0.0 - 1.0)": 1.0,
  "Auto-Play On Attach": true,
  "Boombox Local Offset (x,y,z from player)": "0,1.0,0",
  "Stealth Mode Default (hide from all players)": false,
  "Follow Update Interval (seconds)": 0.1
}
```

## Permission

- `moo.use` — Required to use any `/moo` command

## Files

- `Moo.cs` — The plugin
- `mooo.mp3` — Doja Cat - Mooo! audio file (hosted here for streaming)
