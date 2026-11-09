# FakeCheats

A CounterStrikeSharp plugin for CS2 community servers. The real server keeps
`sv_cheats=0`. When fake cheats are enabled, players receive a client-side
`sv_cheats=1` value to allow supported local visual commands.

Version: **1.0.8**. Requires **CounterStrikeSharp 1.0.379+ with a compatible API
and .NET 10 runtime**. The same managed DLL supports Windows and Linux servers.

## Installation

1. Extract `FakeCheats-1.0.8.zip` and merge its `addons` directory into
   your server's `game/csgo/` directory.
2. Restart the server and check `css_plugins list`.
3. On first load, the plugin creates the main configuration and `messages.json`
   under `addons/counterstrikesharp/configs/plugins/FakeCheats/`.

The installation ZIP contains only the `addons` directory; the README is
available in the source repository. The package includes example JSON files. Existing configuration and message
files are preserved. No `cfg` directory or `server.cfg` is created or modified.

### Upgrading from FakeCheatsWhitelist

Stop the server and remove the old
`addons/counterstrikesharp/plugins/FakeCheatsWhitelist/` plugin directory before
installing FakeCheats. Do not load both names at the same time.

To preserve your settings and translations, rename the existing configuration
directory from `configs/plugins/FakeCheatsWhitelist/` to
`configs/plugins/FakeCheats/` and rename its main file from
`FakeCheatsWhitelist.json` to `FakeCheats.json`. Keep `messages.json` as it is.
All paths are relative to `addons/counterstrikesharp/`. If you do not migrate
the configuration, FakeCheats creates fresh defaults on its first load.

## Enable or disable fake cheats

Use the server console or RCON at any time:

```text
fakecheats_enabled 0
fakecheats_enabled 1
```

`0` disables fake cheats and `1` enables them. The default is `1`. Enter
`fakecheats_enabled` without arguments to query its current value. Other values
are rejected.

If you want a persistent setting, add your preferred line to your existing
`game/csgo/cfg/server.cfg` yourself:

```cfg
sv_cheats 0
fakecheats_enabled 1
```

This is a **command-backed setting**, avoiding native custom ConVar creation
that failed on some servers in version 1.0.3. The console and `server.cfg` syntax
is the same. It is not an engine ConVar and is not available through ConVar-only
lookup or modification commands such as `incrementvar`.

Players with `@css/root` can also use `css_fc_enabled 0` or `css_fc_enabled 1`
from their game console without an RCON password. Both names control the same
global setting. Regular players cannot change it.

Changing the setting immediately sends the current value to connected players.
Connection, spawn, round-start, and periodic synchronization all use the latest
setting. A command received before the world is ready is applied when world
initialization completes. Real server `sv_cheats` remains `0` in either state.

The visual switch is independent of the server command whitelist and root
noclip. Plugin reload resets the switch to its default; reapply your preferred
value after a reload if needed. The next normal execution of `server.cfg` uses
the value you placed there.

Use `fakecheats_enabled 1` to enable fake cheats; setting real `sv_cheats 1`
is unnecessary. If an external command or configuration enables real cheats,
the plugin restores `0` on the next world update, after the engine's ConVar
change callback has returned. It then resends the current visual switch to
players. It never writes a ConVar or sends player updates inside that callback.

## Player commands

With fake cheats enabled, enter supported commands in the **client console**:

```text
mat_fullbright 1
r_csgo_postprocess_enable 0
thirdperson
firstperson
incrementvar mat_fullbright 0 1 1
invprev
```

Use these directly, without a `cmd` prefix. Availability depends on the current
CS2 build: the plugin cannot restore removed commands or remove client-side
development restrictions. `invprev` normally does not require cheats.

| Command | Access | Purpose |
| --- | --- | --- |
| `css_fc_status` | Everyone / server | Show the switch, real server value, synchronization state, and access level |
| `css_fc_sync` | Everyone / server | Resend the current value to yourself, or to everyone when run by the server |
| `css_fc_enabled [0/1]` | Root / server | Query or change the global visual switch |
| `css_fc_noclip` | In-game root | Toggle your own noclip while alive |
| `css_fc_exec <command and arguments>` | In-game root | Dispatch one server command in your own player context |

No join message is sent. The old `ShowConnectMessage` option is ignored.

## Root noclip

Enter **`noclip`** directly in your client console, or retain your existing
`bind "v" "noclip"` binding. Run it again to disable noclip. You must be alive
and have `@css/root`. Regular players are denied, even if `noclip` is accidentally
added to `AdditionalAllowedServerCommands`.

Version 1.0.5 removes only the native server command's `FCVAR_CHEAT` flag after
installing the permission gate, so requests can reach the plugin while real
`sv_cheats` stays `0`. Root requests use the plugin's movement toggle. The native
name and existing bindings are supported; the engine's original callback is
not used for player requests. The flag is restored before hooks are removed on
unload. Other commands' flags are not modified.

`css_fc_noclip` and `css_fc_exec noclip` remain available as alternate entrances.
The plugin sets both player movement fields and sends a network state update,
without enabling real server cheats. It restores its changes on death, respawn,
root permission removal, map change, or plugin unload.

For example, bind a key in your client console:

```text
bind "v" "css_fc_noclip"
```

Native access uses `VEngineCvar007` and the ICvar layout from the s2sdk revision
pinned by CounterStrikeSharp 1.0.379. Future engine ABI changes may require an
update. Startup logs report whether native access was enabled; if lookup fails,
the alternate plugin commands remain available. Engine-side behavior is not
verified by the local native-memory tests. With fake cheats disabled, client-side
checks may still stop the native name before sending it to the server.

Other `css_fc_exec` requests still depend on the engine
and the target command's own checks. This dispatcher accepts one command, with
no semicolons, line breaks, or self-recursion. It does not execute local visual
commands or assign server ConVars.

To grant root access, add `@css/root` to the administrator's existing entry in
`addons/counterstrikesharp/configs/admins.json`, then run `css_admins_reload`:

```json
{
  "Server owner": {
    "identity": "REPLACE_WITH_YOUR_STEAMID64",
    "flags": ["@css/root"],
    "immunity": 100
  }
}
```

Merge this into your existing file. CSS's `@css/*` permission also grants root
access according to its permission rules.

## Language configuration

Edit the main file:

```text
addons/counterstrikesharp/configs/plugins/FakeCheats/FakeCheats.json
```

```json
{
  "ConfigVersion": 1,
  "Language": "en",
  "ReplicateIntervalSeconds": 3,
  "AdditionalAllowedServerCommands": []
}
```

Set `Language` to **`en`** for English or **`zhcn`** for Simplified Chinese.
English is the default, including when upgrading an existing configuration
without a `Language` property. This is one server-wide language selection.

Edit messages in the adjacent UTF-8 file:

```text
addons/counterstrikesharp/configs/plugins/FakeCheats/messages.json
```

Every entry contains both languages, for example:

```json
{
  "noclip_enabled": {
    "en": "[FakeCheats] Noclip enabled.",
    "zhcn": "[FakeCheats] 穿墙已开启。"
  },
  "switch_status": {
    "en": "[FakeCheats] fakecheats_enabled={0}; 0=off, 1=on.",
    "zhcn": "[FakeCheats] fakecheats_enabled={0}；0=关闭，1=开启。"
  }
}
```

The complete default catalog is generated automatically. The packaged
`messages.example.json` is a reference; edit `messages.json` for customization.
Command replies, command help, and normal plugin log messages use this catalog.
Source comments and the README are in English. Chinese text is kept in the
translation data.

Keep numbered placeholders such as `{0}` and `{1}` when translating messages.
They can be reordered. Write `{{` or `}}` for literal braces. Missing entries or
invalid custom formats use the built-in translation for the selected language.
If the entire file cannot be read or parsed, the plugin logs a warning and uses
its built-in catalog. Other language codes are rejected in the main config.

Reload the plugin or restart the server after editing either JSON file. Existing
custom messages are never overwritten during loading or installation.

## Server command whitelist

Unknown server commands are denied for regular players, including `noclip`,
`god`, `give`, `ent_fire`, and `ent_create`. Root bypasses this plugin's gate.
Server console and RCON retain their administrative command access. The plugin
restores real `sv_cheats=0` on the next world update if another configuration
tries to enable it. Real cheats may briefly be active until that update.

Common chat, buying, dropping, team, voting, spectator, and radio commands are
allowed. Movement and shooting use player input data and are unaffected. See
`src/CommandPolicy.cs` for the complete built-in command list.

Add other plugins' regular player commands by exact name, for example:

```json
"AdditionalAllowedServerCommands": ["css_rtv", "css_nominate"]
```

Wildcards such as `*` or `css_*` are rejected. Third-party plugins still need
their own correct permission checks. Denial messages are limited to once every
two seconds per player. `ReplicateIntervalSeconds` must be between 1 and 60.

## Client-side limitations

CSS cannot intercept or strictly whitelist commands executed locally on a
player's client. Spoofing `sv_cheats=1` may enable other local visual commands,
including other targets of local `incrementvar`. This plugin does not claim to
restrict client-local commands to the six listed above.

Explicitly forwarding `mat_fullbright`, `r_csgo_postprocess_enable`, or
`incrementvar` to the server is denied for regular players to prevent changes
to server-wide variables. Normal local execution is unaffected. Disabling fake
cheats sends `sv_cheats=0`; restoration of local visual and camera state depends
on CS2. Root access bypasses this plugin's whitelist, not every engine or
third-party restriction.

The plugin waits for world initialization before reading players and invalidates
queued initialization tasks on map changes or unload. Unload sends `sv_cheats=0`
to connected players and restores noclip changes owned by this plugin.

## Build and validation

With .NET 10 SDK installed, run PowerShell from this project directory:

```powershell
./build.ps1
```

The script runs command policy, lifecycle, and localization checks, compiles the
plugin, and creates `dist/FakeCheats-1.0.8.zip`. Initial dependency
restore requires network access. Source and configuration comments are English.

There is no running CS2 server in the build environment. Compilation and local
checks do not verify live rendering, movement, collision, or network behavior.
After deployment, verify startup, both switch states, root/non-root access,
noclip cleanup, language selection, reconnects, map changes, and hot reload.

## Version 1.0.8

- Removes the limitations notice from `css_fc_status`; the command prints only
  its status summary. Existing message files do not need to be edited.

## Version 1.0.7

- Defers real `sv_cheats` correction and client synchronization until the native
  ConVar change callback has returned, addressing a reentrant write risk in the
  path reported to terminate the server after `sv_cheats 1`.
- Coalesces notifications, suppresses notifications from its own deferred
  correction, and cancels stale work on map changes and unload.
- Adds regression checks that reject writes inside a simulated native callback
  and exercise cancellation, current switch values, and failure recovery. These
  checks do not establish the cause of a native CS2 crash or replace live testing.

## Version 1.0.6

- Renames the plugin, assembly, namespace, configuration directory, and release
  package to FakeCheats. Existing command names and behavior are preserved.
- Documents migration of settings and translations from the previous name.

## Version 1.0.5

- Allows the native `noclip` command name to reach the root permission gate by
  removing only its server-side cheat flag and restoring it on unload.
- Requires root for native noclip even when the additional command list includes
  it. Preserves the global visual switch, translation files, and no-CFG behavior.
- Tests the native interface call, targeted flag modification, and restoration
  against an unmanaged test vtable. Live CS2 verification is still required.

## Version 1.0.4

- Uses a managed command-backed switch to avoid the native custom ConVar
  creation failure reported in version 1.0.3. `fakecheats_enabled 0/1` syntax is
  preserved for the console, RCON, and existing server configurations.
- Adds configurable English and Simplified Chinese messages with English as
  the default and a separate editable message catalog.
- Provides an English README, English source comments, and language format
  fallback handling.
- Generates no game CFG files.
