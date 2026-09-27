using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;

namespace BetterJunimos.Utils {
    /* Crop claim table (anti-clumping)
     *
 * Vanilla work-finding sends every idle Junimo to its nearest actionable tile,
 * so several Junimos leaving the hut together resolve to the same crop and pile
 * onto it. With claims on, the claim-aware end check (PatchFindingCropEnd) treats
 * a tile claimed by ANOTHER Junimo as not actionable, so each Junimo targets the
 * nearest tile nobody is already heading for. When the whole nearby search ball
 * is claimed (crowded), PatchPathfindDoWork falls back to an unfiltered search —
 * vanilla "shadowing" — so behaviour never gets worse than vanilla.
 *
 * Claims are advisory bookkeeping on the master game only: they expire, and every
 * failure path (harvest, dead tile, despawn, day rollover) releases them, so a
 * stale claim can only delay a Junimo, never deadlock it. Farmhands never see
 * claims (the table stays empty on clients), so the filter is a no-op there.
     */
    internal static class CropClaims {
        private class Claim {
            public JunimoHarvester Owner;
            public int Tick;
        }

        // ~30s at 60fps: generous for crossing the largest working radius, yet
        // bounds how long a claim can outlive its action (watering/fertilizing
        // produce no item, so only expiry releases those).
        private const int ExpiryTicks = 1800;

        // tile -> current claim
        private static readonly Dictionary<(GameLocation, Point), Claim> Claims = new();

        // junimo -> its current claim, for releasing when it moves on/despawns
        private static readonly Dictionary<JunimoHarvester, (GameLocation, Point)> OwnerClaims = new();

        // when set, the claim-aware end check behaves as if no claims exist
        // (used by the unfiltered "shadow" search in PatchPathfindDoWork)
        internal static bool SuppressFilter;

        private static bool IsExpired(Claim claim) {
            return Game1.ticks - claim.Tick > ExpiryTicks;
        }

        // is this tile claimed by a junimo other than `self`?
        public static bool IsClaimedByOther(GameLocation location, Point tile, JunimoHarvester self) {
            if (Claims.Count == 0) return false;
            if (!Claims.TryGetValue((location, tile), out var claim)) return false;
            if (IsExpired(claim)) {
                DropClaim(claim.Owner, location, tile);
                return false;
            }
            return !ReferenceEquals(claim.Owner, self);
        }

        public static void TryClaim(GameLocation location, Point tile, JunimoHarvester owner) {
            if (Claims.TryGetValue((location, tile), out var existing) && IsExpired(existing)) {
                DropClaim(existing.Owner, location, tile);
            }

            if (IsClaimedByOther(location, tile, owner)) return;

            // a junimo only works one tile at a time
            ReleaseOwner(owner);
            Claims[(location, tile)] = new Claim { Owner = owner, Tick = Game1.ticks };
            OwnerClaims[owner] = (location, tile);
        }

        public static void ReleaseOwner(JunimoHarvester owner) {
            if (!OwnerClaims.TryGetValue(owner, out var key)) return;
            OwnerClaims.Remove(owner);
            if (Claims.TryGetValue(key, out var claim) && ReferenceEquals(claim.Owner, owner)) {
                Claims.Remove(key);
            }
        }

        private static void DropClaim(JunimoHarvester owner, GameLocation location, Point tile) {
            if (OwnerClaims.TryGetValue(owner, out var ownerKey) &&
                ReferenceEquals(ownerKey.Item1, location) && ownerKey.Item2.Equals(tile)) {
                OwnerClaims.Remove(owner);
            }
            Claims.Remove((location, tile));
        }

        // day start / save load / building list changes: all junimos are gone or
        // reassigned, so nothing should stay claimed
        public static void Clear() {
            Claims.Clear();
            OwnerClaims.Clear();
        }
    }
}
