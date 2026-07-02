using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

/*
 HornOpensDoors

 Goal
 - One honk toggles (open/close) the nearest door strictly in front of, or
   strictly behind, the vehicle.
 - If the door is to the side (vehicle is “sideways” relative to it), it
   will NOT be touched, because we do not scan an area - only a straight
   line.
 - Enforce a cooldown per vehicle so honk spam cannot spam door toggles.

 Why patch EntityVehicle.UseHorn instead of audio

 Vanilla horn use path is:
 - EntityVehicle.OnEntityActivated(..., EntityPlayerLocal _playerFocusing)
 - -> UseHorn(_playerFocusing)

 UseHorn is the real "horn pressed" event. Patching sound playback
 (Audio.Server.Play) is noisy, requires sound name filtering, and is
 not guaranteed to represent "a vehicle honked" (other things can play
 sounds). UseHorn is the cleanest single integration point.

 Important networking note
 - UseHorn is called on the honking player's machine (it takes
   EntityPlayerLocal). That means a dedicated server process usually
   does NOT execute UseHorn for remote players.
 - Therefore this mod intentionally does NOT gate on IsServer.
 - Door toggling is done through TEFeatureDoor.SetOpen, which internally
   performs the same networking vanilla uses:
     - World.SetBlockRPC(...)
     - TileEntity.SetModified() (client -> server or server -> clients)

 ----------------------------------------------------------------------------
 Config

 Loaded via DynamicProperties.Load(modPath, ConfigName), which uses the
 game's own XmlFile loader. The config file format is the standard
 property XML used by 7DTD:

   <HornOpensDoors>
     <property name="ScanDistance" value="12"/>
     <property name="CooldownSeconds" value="3"/>
   </HornOpensDoors>

 If the file is missing or a value is absent/invalid:
 - compiled defaults are kept

 ScanDistance is clamped to 1..12.

 ----------------------------------------------------------------------------
 What the mod does in order

 1) Patch UseHorn

 Harmony Postfix on EntityVehicle.UseHorn.
 Postfix is used so vanilla still plays the horn sound / triggers honk
 events normally, and we only add the door behavior after that.

 2) Per-vehicle cooldown (ticks, not Time.time)

 The cooldown is stored per vehicle entityId.
 To avoid floating time drift and to match vanilla tick-driven systems,
 we use GameTimer.Instance.ticks (20 ticks/sec).

 - CooldownTicks = ceil(CooldownSeconds * 20)
 - If now - last < CooldownTicks => ignore this honk

 3) Direction constraint (front/back only)

 We use vehicle.transform.forward (flattened to XZ plane) as the only
 direction basis.

 We search in two passes:
 - forward line first
 - if nothing toggled, backward line

 4) Line scan (no area scan)

 For each step 1..ScanDistance:
 - compute block cell at origin + direction * step
 - if air: keep going
 - if solid:
     - if it is a tile entity door and is not locked: toggle and stop
     - otherwise stop immediately (line of sight blocked)

 5) Locked / lockpick doors are skipped

 A found door is not toggled if:
 - TEFeatureLockPickable.NeedsLockpicking() == true
 - TEFeatureLockable.IsLocked() == true

 6) Multiblock child handling

 Doors can be multiblock. The ray may hit a child block (BlockValue.ischild).
 In that case we move to the parent block position using the stored
 parent offsets on BlockValue:
 - parent = childPos + (parentx, parenty, parentz)

 ----------------------------------------------------------------------------
 Integration points (for future migration)
 EntityVehicle.UseHorn(EntityPlayerLocal)
 DynamicProperties.Load / ParseInt / ParseFloat
 GameTimer.Instance.ticks
 World.worldToBlockPos(Vector3)
 World.GetBlock(Vector3i)
 World.GetTileEntity(Vector3i)
 TileEntityExtensions.TryGetSelfOrFeature<T>
 TEFeatureDoor.IsOpen / SetOpen
 TEFeatureLockable.IsLocked
 TEFeatureLockPickable.NeedsLockpicking
*/

public sealed class HornOpensDoors : IModApi
{
    internal const string HarmonyId = "HornOpensDoors";
    internal const string ConfigName = "HornOpensDoors";

    internal const int MaxScanDistance = 12;
    internal const float TicksPerSecond = 20f;

    internal static int ScanDistance = 12;
    internal static float CooldownSeconds = 3f;
    internal static ulong CooldownTicks = 60UL;

    public void InitMod(Mod modInstance)
    {
        LoadCfg(modInstance?.Path);
        new Harmony(HarmonyId).PatchAll();
    }

    private static void LoadCfg(string modPath)
    {
        if (!string.IsNullOrEmpty(modPath))
        {
            DynamicProperties p = new DynamicProperties();

            if (p.Load(modPath, ConfigName))
            {
                p.ParseInt("ScanDistance", ref ScanDistance);
                p.ParseFloat("CooldownSeconds", ref CooldownSeconds);
            }
        }

        FixCfg();
    }

    private static void FixCfg()
    {
        ScanDistance = Mathf.Clamp(ScanDistance, 1, MaxScanDistance);
        CooldownSeconds = Mathf.Max(0f, CooldownSeconds);

        CooldownTicks = (CooldownSeconds <= 0f)
            ? 0UL
            : (ulong)Mathf.CeilToInt(CooldownSeconds * TicksPerSecond);
    }
}

[HarmonyPatch(typeof(EntityVehicle), nameof(EntityVehicle.UseHorn))]
public static class Patch_HornOpensDoors
{
    private static readonly Dictionary<int, ulong> lastTick =
        new Dictionary<int, ulong>();

    private static void Postfix(EntityVehicle __instance)
    {
        if (__instance == null)
            return;

        if (!CanRun(__instance.entityId))
            return;

        World world = GameManager.Instance?.World;
        if (world == null)
            return;

        try
        {
            DoToggle(world, __instance);
        }
        catch (Exception ex)
        {
            Log.Error("[HornOpensDoors] " + ex);
        }
    }

    private static bool CanRun(int id)
    {
        if (HornOpensDoors.CooldownTicks == 0UL)
            return true;

        ulong now = GameTimer.Instance.ticks;

        if (lastTick.TryGetValue(id, out ulong last) &&
            now >= last &&
            now - last < HornOpensDoors.CooldownTicks)
        {
            return false;
        }

        lastTick[id] = now;
        return true;
    }

    private static void DoToggle(World world, EntityVehicle v)
    {
        Vector3 fwd = v.transform.forward;
        fwd.y = 0f;

        if (fwd.sqrMagnitude < 0.0001f)
            return;

        fwd.Normalize();

        Vector3 org = v.position;
        org.y += 0.5f;

        if (ToggleLine(world, org, fwd))
            return;

        ToggleLine(world, org, -fwd);
    }

    private static bool ToggleLine(World world, Vector3 org, Vector3 dir)
    {
        for (int i = 1; i <= HornOpensDoors.ScanDistance; i++)
        {
            Vector3i p = World.worldToBlockPos(org + dir * i);

            BlockValue bv = world.GetBlock(p);
            if (bv.isair)
                continue;

            ITileEntity te = world.GetTileEntity(ParentPos(p, bv));

            if (!te.TryGetSelfOrFeature<TEFeatureDoor>(out TEFeatureDoor door))
                return false;

            if (te.TryGetSelfOrFeature<TEFeatureLockPickable>(
                    out TEFeatureLockPickable lp) &&
                lp.NeedsLockpicking())
            {
                return false;
            }

            if (te.TryGetSelfOrFeature<TEFeatureLockable>(
                    out TEFeatureLockable lk) &&
                lk.IsLocked())
            {
                return false;
            }

            door.SetOpen(!door.IsOpen(), true);
            return true;
        }

        return false;
    }

    private static Vector3i ParentPos(Vector3i p, BlockValue bv)
    {
        if (!bv.ischild)
            return p;

        return new Vector3i(
            p.x + bv.parentx,
            p.y + bv.parenty,
            p.z + bv.parentz
        );
    }
}