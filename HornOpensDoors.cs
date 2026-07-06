using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

/*
 HornOpensDoors

 Goal
 -----
 One honk toggles open or close on the nearest door strictly in front of
 or strictly behind the vehicle
 Enforce a cooldown per vehicle so honk spam cannot spam door toggles
 Only the driver can toggle doors by honking, passengers cannot

 Why patch EntityVehicle.UseHorn instead of audio
 -------------------------------------------------
 UseHorn is the real horn pressed event Patching sound playback is noisy
 requires sound name filtering and is not guaranteed to represent a
 vehicle honked UseHorn is the cleanest single integration point

 Important networking note
 --------------------------
 UseHorn is called on the honking player's machine Door toggling is done
 through TEFeatureDoor.SetOpen which already includes local client side
 prediction animation and sound before sending the network update

 What the mod does in order
 ---------------------------
 1 Patch UseHorn
   Harmony Postfix on EntityVehicle.UseHorn so vanilla still plays the
   horn sound and we only add the door behavior after that

 2 Driver check
   Compare the honking player against whoever is attached in seat 0 the
   driver seat If they do not match a passenger honked or nobody is
   driving so we exit

 3 Per vehicle cooldown using ticks
   Cooldown is stored per vehicle entityId using GameTimer ticks at 20
   ticks per second If now minus last is less than CooldownTicks the
   honk is ignored

 4 Parallelepiped scan front and back only
   We use vehicle.transform.forward flattened to the XZ plane We scan in
   two passes forward box first then backward box if nothing toggled
   The box is 5 blocks wide and 3 blocks high extending up to
   ScanDistance blocks forward This covers a wide area without complex
   math and catches doors slightly off center from the vehicle path

 5 Locked and lockpick doors are skipped
   A found door is not toggled if TEFeatureLockPickable.NeedsLockpicking
   returns true or TEFeatureLockable.IsLocked returns true The scan
   continues to find an unlocked door in the area

 6 Multiblock child handling
   Doors can be multiblock The scan may hit a child block where
   BlockValue.ischild is true In that case we move to the parent block
   position using BlockValue.parent

 Integration points
 -------------------
 EntityVehicle.UseHorn(EntityPlayerLocal)
 EntityVehicle.HasDriver
 EntityVehicle.GetAttached(int)
 DynamicProperties.Load / ParseInt / ParseFloat
 GameTimer.Instance.ticks
 World.worldToBlockPos(Vector3)
 World.GetBlock(Vector3i)
 World.GetTileEntity(Vector3i)
 TileEntityExtensions.TryGetSelfOrFeature<T>
 TEFeatureDoor.IsOpen / SetOpen
 TEFeatureLockable.IsLocked
 TEFeatureLockPickable.NeedsLockpicking
 BlockValue.ischild / parent
*/

public sealed class HornOpensDoors : IModApi
{
    internal const string HarmonyId = "HornOpensDoors";
    internal const string ConfigName = "HornOpensDoors";

    internal const int MaxScanDistance = 25;
    internal const float MaxCooldownSeconds = 3f;
    internal const float TicksPerSecond = 20f;

    internal static int ScanDistance = MaxScanDistance;
    internal static float CooldownSeconds = MaxCooldownSeconds;
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

        CooldownTicks = (ulong)Mathf.CeilToInt(CooldownSeconds * TicksPerSecond);
    }
}

[HarmonyPatch(typeof(EntityVehicle), nameof(EntityVehicle.UseHorn))]
public static class Patch_HornOpensDoors
{
    private static readonly Dictionary<int, ulong> lastTick =
        new Dictionary<int, ulong>();

    private static void Postfix(
        EntityVehicle __instance,
        EntityPlayerLocal player)
    {
        World world = GameManager.Instance?.World;

        if (__instance == null || player == null || world == null
            || !IsDriver(__instance, player) || !CanRun(__instance.entityId))
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

    private static bool IsDriver(EntityVehicle v, EntityPlayerLocal player)
    {
        return v.HasDriver && v.GetAttached(0) == player;
    }

    private static bool CanRun(int id)
    {
        if (HornOpensDoors.CooldownTicks == 0UL)
            return true;

        ulong now = GameTimer.Instance.ticks;

        if (lastTick.TryGetValue(id, out ulong last)
            && now >= last
            && now - last < HornOpensDoors.CooldownTicks)
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

        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

        Vector3 org = v.position;
        org.y += 0.5f;

        if (ToggleBox(world, org, fwd, right))
            return;

        ToggleBox(world, org, -fwd, right);
    }

    private static bool ToggleBox(World world, Vector3 org, Vector3 dir, Vector3 right)
    {
        for (int i = 1; i <= HornOpensDoors.ScanDistance; i++)
        {
            Vector3 step = org + dir * i;

            for (int h = -1; h <= 1; h++)
            {
                for (int w = -2; w <= 2; w++)
                {
                    Vector3 pos = step + right * w + Vector3.up * h;
                    Vector3i p = World.worldToBlockPos(pos);

                    BlockValue bv = world.GetBlock(p);
                    if (bv.isair)
                        continue;

                    Vector3i tilePos = bv.ischild ? p + bv.parent : p;
                    ITileEntity te = world.GetTileEntity(tilePos);

                    if (te == null)
                        continue;

                    if (!te.TryGetSelfOrFeature(out TEFeatureDoor door))
                        continue;

                    if (te.TryGetSelfOrFeature(out TEFeatureLockPickable lp)
                        && lp.NeedsLockpicking())
                        continue;

                    if (te.TryGetSelfOrFeature(out TEFeatureLockable lk)
                        && lk.IsLocked())
                        continue;

                    door.SetOpen(!door.IsOpen(), true);
                    return true;
                }
            }
        }

        return false;
    }
}