# Configurable Ores
This is a Space Engineers mod for managing planetary ores. Full information available at the Steam Workshop page: [Configurable Ores](https://steamcommunity.com/sharedfiles/filedetails/?id=2973891097)

![se-configurable-ores Steam Workshop thumbnail image](https://github.com/padthaitofuhot/se-configurable-ores/blob/b412c5fc39d2db669325a083f147048979a5b0da/thumb.jpg)

## This fork: in-game settings menu

This fork adds a settings menu built on [Rich HUD Master](https://steamcommunity.com/workshop/filedetails/?id=1965654081). Open the Rich HUD settings menu in game and pick **Configurable Ores**.

### What's in the menu

- **Status**: whether you can edit, whether a restart is needed, the result of your last change, and buttons to regenerate ore placement or reset world settings.
- **World depth / World size**: the settings that apply to every planet. Changing a world setting also overwrites that setting on every planet.
- **Planets**: pick a planet, add or remove ores, and set depth (min, max) and size for that planet. Turn off "Use detector range" there for Max depth to apply. With a detector-range mod, setting depth per planet is the way to keep ore reachable.
- **Planet ores**: pick a planet and one of its ores, then set its rarity and override its depth and size.
- **Mod**: logging, hiding the menu from non-admins, and disabling chat commands.
- **Help**: what each setting does.

Ore placement only changes when the world loads, so restart the world after making changes. The menu says when a restart is needed.

### Who can change settings

The server makes every change. The menu and `/ore` chat commands both send the command to the server, which checks that the player is **Admin** or higher, applies it with the same checks as before, saves it, and sends the new settings to every player.

- Players who aren't admins see the menu read-only. An admin can hide it from them completely with **Mod > Hide this menu from non-admins** (or `/ore world mod hidemenu on`).
- `/ore` chat commands now work for admins on dedicated servers too.

### Without Rich HUD Master

The mod works as before without Rich HUD Master; there's just no menu. Chat commands still work.

### Building and publishing

- The Rich HUD client code is vendored in `Data/Scripts/ConfigurableOres/RichHudFramework` (MIT, see the LICENSE file there).
- `ConfigurableOres.csproj` reads the game's `Bin64` folder from the `SEBin64` property, for example `dotnet build -p:SEBin64="C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64"`. The project file is only for IDE support; the game compiles the scripts itself.
- When publishing to the Workshop, add Rich HUD Master (1965654081) as a required item so players get it automatically.
- Don't run this fork and the original mod in the same world. They share the same config file and network channel.
