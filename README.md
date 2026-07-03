# HornOpensDoors

Harmony mod for 7 Days to Die that lets your vehicle horn open and close doors

Tested on 3.0

## Problem

You're driving up to your base gate in a car or gyro and have to get out just to open the door then get back in Annoying if you do it every single trip Real vehicles have this in every game with a garage door so why not here

## What it does

- Honk the horn while facing a door in front or behind your vehicle and it toggles open or closed
- Only works in a straight line directly in front or directly behind the vehicle doors off to the side are ignored on purpose
- Locked doors and doors that need lockpicking are never touched
- Cooldown per vehicle so spamming the horn doesn't spam the door open and shut

## How it works

Hooks into the vehicle's actual horn press event rather than sound playback so it's not guessing based on audio Vanilla horn behavior is untouched the mod just adds the door check on top

When you honk it traces a straight line forward from the vehicle first if nothing happens it checks backward Doors need to be directly in that line not to the side and not through walls the first solid block in the way either is the door or blocks the check entirely

## Config

Comes with a small config file

```xml
<HornOpensDoors>
  <property name="ScanDistance" value="12"/>
  <property name="CooldownSeconds" value="3"/>
</HornOpensDoors>
```

ScanDistance is how many blocks forward/backward it checks for a door capped at 12
CooldownSeconds is how long you have to wait before honking toggles a door again on the same vehicle

If the config is missing or broken it just falls back to defaults

## Install

Drop the folder in Mods That's it

## Compatibility

Only touches EntityVehicle.UseHorn as a postfix so vanilla horn sound and behavior stay exactly the same Door toggling goes through the same networked door system vanilla uses so it should work fine in multiplayer without special server setup

Built against 3.0 internals Might break on other versions if these change
```
