using StardewValley;
using Microsoft.Xna.Framework;
using StardewValley.Characters;
using HarmonyLib;
using BetterJunimos.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Netcode;
using BetterJunimos.Abilities;
using StardewModdingAPI;
using StardewValley.Buildings;
using StardewValley.Pathfinding;
using SObject = StardewValley.Object;

namespace BetterJunimos.Patches {
    /* foundCropEndFunction
     *
     * Is there an action to perform at the end of this pathfind?
     * Completely replace
     */
    public class PatchFindingCropEnd {
        public static bool Prefix(JunimoHarvester __instance, ref PathNode currentNode, ref NetGuid ___netHome, ref bool __result) {
            __result = Util.Abilities.IsActionable(__instance.currentLocation, new Vector2(currentNode.x, currentNode.y), ___netHome.Value);

            // claim filter: another junimo is already heading for this tile — treat it
            // as taken so this junimo targets the next-nearest work instead of piling
            // onto the same crop. Claims only exist on the master game (and can be
            // suspended for the unfiltered "shadow" search), so this is a no-op
            // for farmhands and when the option is off.
            if (__result && BetterJunimos.Config.JunimoImprovements.UseCropClaims && !CropClaims.SuppressFilter && Context.IsMainPlayer) {
                __result = !CropClaims.IsClaimedByOther(__instance.currentLocation, new Point(currentNode.x, currentNode.y), __instance);
            }

            return false;
        }
    }

    /* tryToHarvestHere
     *
     * Try to perform ability
     * Except harvest
     * Completely replace
     *
     */
    public class PatchTryToHarvestHere {
        public static bool Prefix(JunimoHarvester __instance, ref int ___harvestTimer, ref NetGuid ___netHome) {
            if (!Context.IsMainPlayer) return true;
            var hut = Util.GetHutFromId(__instance.HomeId);
            // If the hut can't be found (e.g. it was demolished), fall back to the
            // vanilla behaviour instead of freezing the junimo in place.
            if (hut is null) return true;
            var id = __instance.HomeId;
            var pos = __instance.Tile;
            int time;
            var junimoAbility = Util.Abilities.IdentifyJunimoAbility(__instance.currentLocation, pos, id);
            if (junimoAbility != null) {
                if (junimoAbility is HarvestBushesAbility) {
                    // Use the update() harvesting
                    time = 2000;
                } else if (!Util.Abilities.PerformAction(junimoAbility, id, __instance.currentLocation, pos, __instance)) {
                    // didn't succeed, move on
                    time = 0;

                    // add failed action to ability cooldowns
                    Util.Abilities.ActionFailed(__instance.currentLocation, junimoAbility, pos);
                } else {
                    // succeeded, shake
                    if (junimoAbility is HarvestCropsAbility) {
                        time = 2000;
                    }
                    else if (BetterJunimos.Config.JunimoImprovements.WorkRidiculouslyFast)
                        time = 20;
                    else
                        time = Util.Progression.WorkFaster ? 300 : 998;
                }
            } else {
                // nothing to do here (the work vanished before we arrived):
                // free this junimo's claim on the tile, then re-decide
                if (BetterJunimos.Config.JunimoImprovements.UseCropClaims) CropClaims.ReleaseOwner(__instance);
                time = Util.Progression.WorkFaster ? 5 : 200;
                __instance.pokeToHarvest();
            }

            ___harvestTimer = time;

            return false;
        }
    }

    // update
    // Animate & handle action timer 
    public class PatchJunimoShake {
        public static void Postfix(JunimoHarvester __instance, ref int ___harvestTimer) {
            if (!Context.IsMainPlayer) return;

            if (Util.Progression.WorkFaster && ___harvestTimer == 999) {
                // skip last second of harvesting if faster
                ___harvestTimer = 0;
            } else if (___harvestTimer is > 500 and < 1000 || Util.Progression.WorkFaster && ___harvestTimer > 5) {
                __instance.shake(50);
            }
        }
    }

    /* tryToAddItemToHut
     *
     * Botanist profession: forage (e.g. wild seed crops) harvested by Junimos
     * should be iridium quality, matching what the player would get harvesting
     * them personally. This runs before the item is placed in the hut chest, so
     * the quality is applied to the item as stored (and to raisin double-harvest
     * copies, which copy this item's quality).
     *
     * Bush yields are not affected, matching vanilla: the game only lets Junimos
     * harvest bushes by creating item 815 (tea leaves), and bush berries (296
     * salmonberry, 410 blackberry) never get quality from Botanist.
     */
    public class PatchJunimoHarvesterAddItemToHut {
        public static void Prefix(JunimoHarvester __instance, Item i) {
            // the junimo finished harvesting its claimed tile, free it up
            CropClaims.ReleaseOwner(__instance);

            if (!Game1.player.professions.Contains(16)) return;
            if (i is SObject obj && obj.Category == SObject.GreensCategory && obj.ItemId is not ("815" or "296" or "410") && obj.Quality < 4) {
                obj.Quality = 4;
            }
        }
    }

    // pathfindToRandomSpotAroundHut
    // Expand radius of random pathfinding
    public class PatchPathfindToRandomSpotAroundHut {
        public static void Postfix(JunimoHarvester __instance) {
            var hut = Util.GetHutFromId(__instance.HomeId);
            if (hut is null) return;

            var radius = Util.CurrentWorkingRadius;
            var retry = 0;
            // Perf fix (ported from Aufhcegak/BetterJunimosFix): each failed retry runs a
            // full A* pathfind, and unreachable endpoints (fences, buildings) used to retry
            // 6 times per junimo per event. Cap at 2 attempts; success path is unchanged.
            do {
                var endPoint = __instance.currentLocation.IsGreenhouse ?
                    EndPointInGreenhouse(__instance) : EndPointInFarm(hut, radius);

                // BetterJunimos.SMonitor.Log($"PatchPathfindToRandomSpotAroundHut: " +
                //                            $"#{__instance.whichJunimoFromThisHut} " +
                //                            $"in {__instance.currentLocation.Name} " +
                //                            $"from [{__instance.getTileX()} {__instance.getTileX()}] " +
                //                            $"to [{endPoint.X} {endPoint.Y}]",
                //     LogLevel.Debug);

                __instance.controller = new PathFindController(__instance, __instance.currentLocation, endPoint, -1, __instance.reachFirstDestinationFromHut, 100);
                retry++;
            } while (retry <= 1 && __instance.controller?.pathToEndPoint == null);
        }

        private static Point EndPointInGreenhouse(JunimoHarvester jh) {
            var gw = jh.currentLocation.map.Layers[0].LayerWidth;
            var gh = jh.currentLocation.map.Layers[0].LayerHeight;
            // Bug fix (ported from Aufhcegak/BetterJunimosFix): the original swapped the
            // width/height bounds (`Next(-(gw / 2 + 2), gh / 2 - 2)` for both axes), which
            // throws ArgumentOutOfRangeException on non-standard greenhouse sizes. Use a
            // symmetric per-axis range that always lands inside the walls.
            var x = gw / 2 + Game1.random.Next(-(gw / 2 - 2), gw / 2 - 2);
            var y = gh / 2 + Game1.random.Next(-(gh / 2 - 2), gh / 2 - 2);
            return new Point(x, y);
        }

        private static Point EndPointInFarm(JunimoHut hut, int radius) {
            return Utility.Vector2ToPoint(new Vector2(hut.tileX.Value + 1 + Game1.random.Next(-radius, radius + 1), hut.tileY.Value + 1 + Game1.random.Next(-radius, radius + 1)));
        }
    }

    [HarmonyPriority(Priority.Low)]
    public class PatchGet_home {
        public static void Postfix(ref JunimoHut __result, ref NetGuid ___netHome) {
            __result = Util.GetHutFromId(___netHome.Value);
        }
    }

    [HarmonyPriority(Priority.Low)]
    public class PatchSet_home {
        public static void Postfix(JunimoHut value, ref NetGuid ___netHome) {
            ___netHome.Value = Util.GetHutIdFromHut(value);
        }
    }

    // pathfindToNewCrop - completely replace
    // Remove the max distance boundary
    [HarmonyPriority(Priority.Low)]
    public class PatchPathfindDoWork {
        // decision rate cap: >=0.33s between decisions after a successful search,
        // >=0.66s after a failed one. Vanilla decisions are event-driven (arrival,
        // harvest-timer expiry, the hut's 10-minute poke) so this rarely binds in
        // normal play; its job is to bound redundant re-planning when triggers
        // coincide (every junimo poked in the same frame) and to back off after
        // failed searches in fenced-off areas instead of retrying immediately.
        private const int DecisionIntervalTicks = 20;
        private const int FailedDecisionBackoffTicks = 40;
        private static readonly Dictionary<JunimoHarvester, int> NextDecisionTick = new();

        internal static void ClearDecisionTimers() {
            NextDecisionTick.Clear();
        }


        public static bool Prefix(JunimoHarvester __instance, ref NetEvent1Field<int, NetInt> ___netAnimationEvent) {
            if (!Context.IsMainPlayer) return true;
            var hut = Util.GetHutFromId(__instance.HomeId);
            if (hut is null) return true;

            var quittingTime = Util.Progression.CanWorkInEvenings ? 2400 : 1900;


            if (Game1.timeOfDay > quittingTime) {
                // bedtime, all Junimos return to huts and/or despawn
                Util.Progression.PromptForCanWorkInEvenings();
                if (__instance.controller != null) return false;

                if (__instance.currentLocation.NameOrUniqueName == hut.GetParentLocation().NameOrUniqueName) {
                    __instance.returnToJunimoHut(__instance.currentLocation);
                }

                if (__instance.currentLocation.IsGreenhouse) {
                    returnToGreenhouseDoor(__instance, __instance.currentLocation);
                } else {
                    // can't walk back to the hut from here, just despawn
                    __instance.junimoReachedHut(__instance, __instance.currentLocation);
                }
            }

            // Prevent working when not paid
            else if (BetterJunimos.Config.JunimoPayment.WorkForWages && !Util.Payments.WereJunimosPaidToday) {
                if (Game1.random.NextDouble() < 0.02) {
                    __instance.pathfindToRandomSpotAroundHut();
                } else {
                    // go on strike
                    ___netAnimationEvent.Fire(7);
                }
            } else {
                // ---- decision rate cap ----
                // Vanilla decisions are event-driven (path arrival, harvest-timer
                // expiry, the hut's 10-minute poke), so per-junimo cadence is low.
                // The cap matters when triggers coincide: the 10-minute poke re-plans
                // every junimo in the same frame, and a failed search followed by
                // immediate re-pokes (fenced-in areas) would otherwise re-run full
                // A* back to back. In between, a walking junimo keeps its path.
                var now = Game1.ticks;
                if (NextDecisionTick.TryGetValue(__instance, out var nextAllowed) && now < nextAllowed) {
                    return false; // keep the current path / stand still a moment
                }

                if (hut.noHarvest.Value || (Game1.random.NextDouble() < 0.035 && !BetterJunimos.Config.JunimoImprovements.WorkRidiculouslyFast)) {
                    // occasional random stroll
                    NextDecisionTick[__instance] = now + DecisionIntervalTicks;
                    __instance.pathfindToRandomSpotAroundHut();
                    return false;
                }

                // ---- scan gate ----
                // The hut work scan is cached for 60 ticks; consult it before spending
                // two full-budget A* searches. Scan false => no actionable tile inside
                // the radius box, and endpoints outside the box are discarded anyway,
                // so the searches below could never succeed.
                var hasWork = hut.areThereMatureCropsWithinRadius();
                if (!hasWork) {
                    NextDecisionTick[__instance] = now + DecisionIntervalTicks;
                    VanillaFallbackRoll(__instance, ___netAnimationEvent, hut, tryLkc: false);
                    return false;
                }

                // walk to work? With crop claims on, the end check (PatchFindingCropEnd)
                // ignores tiles another junimo has claimed, so junimos leaving the hut
                // together pick different targets instead of converging on one crop.
                __instance.controller = new PathFindController(__instance, __instance.currentLocation, __instance.foundCropEndFunction, -1, __instance.reachFirstDestinationFromHut,
                    100, Point.Zero);

                var radius = Util.CurrentWorkingRadius;
                var outsideRadius = __instance.controller.pathToEndPoint is not null && hut.tileX is not null && hut.tileY is not null && __instance.currentLocation is not null &&
                    __instance.currentLocation.NameOrUniqueName == hut.GetParentLocation().NameOrUniqueName && (
                        Math.Abs(__instance.controller.pathToEndPoint.Last().X - hut.tileX.Value - 1) > radius || Math.Abs(__instance.controller.pathToEndPoint.Last().Y - hut.tileY.Value - 1) > radius);

                if (BetterJunimos.Config.JunimoImprovements.UseCropClaims && __instance.controller.pathToEndPoint is null) {
                    // everything actionable nearby is claimed by other junimos (crowded):
                    // rerun the search unfiltered so crowding shadows exactly like vanilla
                    // instead of degrading, before falling any further back
                    CropClaims.SuppressFilter = true;
                    __instance.controller = new PathFindController(__instance, __instance.currentLocation, __instance.foundCropEndFunction, -1, __instance.reachFirstDestinationFromHut,
                        100, Point.Zero);
                    CropClaims.SuppressFilter = false;

                    outsideRadius = __instance.controller.pathToEndPoint is not null && hut.tileX is not null && hut.tileY is not null && __instance.currentLocation is not null &&
                        __instance.currentLocation.NameOrUniqueName == hut.GetParentLocation().NameOrUniqueName && (
                            Math.Abs(__instance.controller.pathToEndPoint.Last().X - hut.tileX.Value - 1) > radius || Math.Abs(__instance.controller.pathToEndPoint.Last().Y - hut.tileY.Value - 1) > radius);
                }

                if (__instance.controller.pathToEndPoint != null && !outsideRadius) {
                    NextDecisionTick[__instance] = now + DecisionIntervalTicks;

                    // Junimo has somewhere to be, let it happen
                    if (BetterJunimos.Config.JunimoImprovements.UseCropClaims &&
                        !CropClaims.IsClaimedByOther(__instance.currentLocation, __instance.controller.pathToEndPoint.Last(), __instance)) {
                        // claim the endpoint so later decisions by other junimos skip it
                        // (guaranteed to succeed: the filtered search already skipped claims)
                        CropClaims.TryClaim(__instance.currentLocation, __instance.controller.pathToEndPoint.Last(), __instance);
                    }

                    ___netAnimationEvent.Fire(0);
                } else {
                    // search failed: release any stale claim and back off longer before
                    // the next attempt (fenced-off/empty areas are where the old code
                    // burned 2-3 full-budget A* every frame)
                    NextDecisionTick[__instance] = now + FailedDecisionBackoffTicks;
                    if (BetterJunimos.Config.JunimoImprovements.UseCropClaims) CropClaims.ReleaseOwner(__instance);

                    // Junimo has no path, or path endpoint is outside the hut radius
                    VanillaFallbackRoll(__instance, ___netAnimationEvent, hut, tryLkc: true);
                }
            }

            return false;
        }

        // the vanilla fallback tail: 50% to the last known work tile, 25% home, else wander
        private static void VanillaFallbackRoll(JunimoHarvester __instance, NetEvent1Field<int, NetInt> ___netAnimationEvent, JunimoHut hut, bool tryLkc) {
            Util.Abilities.lastKnownCropLocations.TryGetValue((hut, __instance.currentLocation), out var lkc);
            if (tryLkc && Game1.random.NextDouble() < 0.5 && !lkc.Equals(Point.Zero)) {
                // hut has some work to do, send Junimo there
                __instance.controller = new PathFindController(__instance, __instance.currentLocation, lkc, -1, __instance.reachFirstDestinationFromHut, 100);
            } else if (Game1.random.NextDouble() < 0.25) {
                // unlucky, send Junimo home
                ___netAnimationEvent.Fire(0);

                if (__instance.currentLocation is Farm) {
                    __instance.returnToJunimoHut(__instance.currentLocation);
                } else if (__instance.currentLocation.IsGreenhouse) {
                    returnToGreenhouseDoor(__instance, __instance.currentLocation);
                } else {
                    // can't walk back to the hut from here, just despawn
                    __instance.junimoReachedHut(__instance, __instance.currentLocation);
                }
            } else {
                // move Junimo randomly
                __instance.pathfindToRandomSpotAroundHut();
            }
        }

        private static void returnToGreenhouseDoor(JunimoHarvester junimo, GameLocation location) {
            if (Utility.isOnScreen(Utility.Vector2ToPoint(junimo.position.Value / 64f), 64, junimo.currentLocation)) junimo.jump();
            junimo.collidesWithOtherCharacters.Value = false;

            if (Game1.IsMasterGame) {
                var door = GreenhouseDoor(junimo, location);
                if (door == Point.Zero) {
                    junimo.junimoReachedHut(junimo, junimo.currentLocation);
                    return;
                }

                junimo.controller = new PathFindController(junimo, location, door, 1, junimo.junimoReachedHut);
                if (junimo.controller.pathToEndPoint == null || junimo.controller.pathToEndPoint.Count == 0) {
                    junimo.junimoReachedHut(junimo, junimo.currentLocation);
                    return;
                }
            }

            if (!Utility.isOnScreen(Utility.Vector2ToPoint(junimo.position.Value / 64f), 64, junimo.currentLocation)) return;
            location.playSound("junimoMeep1");
        }

        public static Point GreenhouseDoor(JunimoHarvester junimo, GameLocation location) {
            //TryFind warp to hutlocation
            var warp = location.warps.FirstOrDefault(warp => warp.TargetName == junimo.home.GetParentLocation().NameOrUniqueName);
            if (warp != null) {
                return new Point(warp.X, warp.Y - 1);
            }

            return Point.Zero;
        }
    }

    // pokeToHarvest
    //public void pokeToHarvest()
    public class PatchPokeToHarvest {
        public static void Postfix(JunimoHarvester __instance, bool ___destroy) {
            if (___destroy) {
                // despawning: don't hold a work tile hostage
                CropClaims.ReleaseOwner(__instance);
                return;
            }
            if (__instance.controller != null) return;
            if (!BetterJunimos.Config.JunimoImprovements.WorkRidiculouslyFast) return;
            __instance.pathfindToNewCrop();
        }
    }
}