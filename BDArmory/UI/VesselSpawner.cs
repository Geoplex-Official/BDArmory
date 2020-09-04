using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;
using BDArmory.Control;
using BDArmory.Core;
using BDArmory.Modules;
using BDArmory.Misc;

namespace BDArmory.UI
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class VesselSpawner : MonoBehaviour
    {
        public static VesselSpawner Instance;

        private string message;
        void Awake()
        {
            if (Instance)
                Destroy(Instance);
            Instance = this;
        }

        private void OnGUI()
        {
        }

        public enum SpawnFailureReason { None, NoCraft, NoTerrain, InvalidVessel, VesselLostParts, VesselFailedToSpawn, TimedOut };
        public SpawnFailureReason spawnFailureReason = SpawnFailureReason.None;
        public bool vesselsSpawning = false;
        public bool vesselSpawnSuccess = false;
        public int spawnedVesselCount = 0;
        public void SpawnAllVesselsOnce(Vector2d geoCoords, double altitude = 0, float spawnDistanceFactor = 10f, float easeInSpeed = 1f, bool killEverythingFirst = true, string spawnFolder = null)
        {
            vesselsSpawning = true; // Signal that we've started the spawning vessels routine.
            vesselSpawnSuccess = false; // Set our success flag to false for now.
            spawnFailureReason = SpawnFailureReason.None;
            if (spawnAllVesselsOnceCoroutine != null)
                StopCoroutine(spawnAllVesselsOnceCoroutine);
            spawnAllVesselsOnceCoroutine = StartCoroutine(SpawnAllVesselsOnceCoroutine(geoCoords, altitude, spawnDistanceFactor, easeInSpeed, killEverythingFirst, spawnFolder));
            Debug.Log("[VesselSpawner]: Triggering vessel spawning at " + BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.ToString("G6") + ", with altitude " + altitude + "m.");
        }

        // Cancel both spawning modes.
        public void CancelVesselSpawn()
        {
            if (spawnAllVesselsOnceCoroutine != null)
            {
                StopCoroutine(spawnAllVesselsOnceCoroutine);
                spawnAllVesselsOnceCoroutine = null;
            }
            if (vesselsSpawning)
            {
                vesselsSpawning = false;
                message = "Vessel spawning cancelled.";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.Log("[VesselSpawner]: " + message);
            }
            if (vesselsSpawningContinuously)
            {
                vesselsSpawningContinuously = false;
                if (continuousSpawningScores != null)
                    DumpContinuousSpawningScores();
                continuousSpawningScores = null;
                message = "Continuous vessel spawning cancelled.";
                Debug.Log("[VesselSpawner]: " + message);
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                BDACompetitionMode.Instance.ResetCompetitionScores();
            }
            if (spawnVesselsContinuouslyCoroutine != null)
            {
                StopCoroutine(spawnVesselsContinuouslyCoroutine);
                spawnVesselsContinuouslyCoroutine = null;
            }
        }

        // TODO Make an option to spawn once at altitude without lowering to the ground for places where taking off is difficult.
        private Coroutine spawnAllVesselsOnceCoroutine;
        // Spawns all vessels in an outward facing ring and lowers them to the ground. An altitude of 5m should be suitable for most cases.
        private IEnumerator SpawnAllVesselsOnceCoroutine(Vector2d geoCoords, double altitude, float spawnDistanceFactor, float easeInSpeed, bool killEverythingFirst, string spawnFolder = null)
        {
            #region Initialisation and sanity checks
            // Tally up the craft to spawn.
            var crafts = Directory.GetFiles(Environment.CurrentDirectory + $"/AutoSpawn/{spawnFolder}").Where(f => f.EndsWith(".craft")).ToList();
            if (crafts.Count == 0)
            {
                message = "Vessel spawning: found no craft files in " + Environment.CurrentDirectory + $"/AutoSpawn/{spawnFolder}";
                Debug.Log("[VesselSpawner]: " + message);
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                vesselsSpawning = false;
                spawnFailureReason = SpawnFailureReason.NoCraft;
                yield break;
            }
            crafts.Shuffle(); // Randomise the spawn order.
            spawnedVesselCount = 0; // Reset our spawned vessel count.
            altitude = Math.Max(2, altitude); // Don't spawn too low.
            message = "Spawning " + crafts.Count + " vessels at an altitude of " + altitude.ToString("G0") + "m" + (crafts.Count > 8 ? ", this may take some time..." : ".");
            Debug.Log("[VesselSpawner]: " + message);
            var spawnAirborne = altitude > 10;
            if (BDACompetitionMode.Instance) // Reset competition stuff.
            {
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                BDACompetitionMode.Instance.LogResults("due to spawning.", "auto-dump-from-spawning"); // Log results first.
                BDACompetitionMode.Instance.StopCompetition();
                BDACompetitionMode.Instance.ResetCompetitionScores(); // Reset competition scores.
            }
            yield return new WaitForFixedUpdate();
            #endregion

            #region Pre-spawning
            if (killEverythingFirst)
            {
                // Kill all vessels (including debris).
                var vesselsToKill = new List<Vessel>(FlightGlobals.Vessels);
                foreach (var vessel in vesselsToKill)
                    RemoveVessel(vessel);
            }
            while (removeVesselsPending > 0)
                yield return new WaitForFixedUpdate();
            #endregion

            #region Spawning
            // Get the spawning point in world position coordinates.
            var terrainAltitude = FlightGlobals.currentMainBody.TerrainAltitude(geoCoords.x, geoCoords.y);
            var spawnPoint = FlightGlobals.currentMainBody.GetWorldSurfacePosition(geoCoords.x, geoCoords.y, terrainAltitude + altitude);
            var surfaceNormal = FlightGlobals.currentMainBody.GetSurfaceNVector(geoCoords.x, geoCoords.y);
            var localSurfaceNormal = surfaceNormal;
            Ray ray;
            RaycastHit hit;

            if (killEverythingFirst)
            {
                // Update the floating origin offset, so that the vessels spawn within range of the physics.
                FloatingOrigin.SetOffset(spawnPoint); // This adjusts local coordinates, such that spawnPoint is (0,0,0).
                var flightCamera = FlightCamera.fetch;
                flightCamera.SetCamCoordsFromPosition(2 * spawnDistanceFactor * (1 + crafts.Count) * surfaceNormal);
                flightCamera.transform.rotation = Quaternion.FromToRotation(flightCamera.transform.up, -surfaceNormal) * flightCamera.transform.rotation;
                yield return new WaitForFixedUpdate(); // Give it a moment to start loading in terrain.

                if (terrainAltitude > 0) // Not over the ocean or on a surfaceless body.
                {
                    // Wait for the terrain to load in before continuing.
                    var testPosition = 1000f * surfaceNormal;
                    var terrainDistance = testPosition.magnitude;
                    var lastTerrainDistance = terrainDistance;
                    ray = new Ray(testPosition, -surfaceNormal);
                    message = "Waiting up to 10s for terrain to settle.";
                    Debug.Log("[VesselSpawner]: " + message);
                    BDACompetitionMode.Instance.competitionStatus.Add(message);
                    var startTime = Planetarium.GetUniversalTime();
                    double lastStableTimeStart = startTime;
                    double stableTime = 0;
                    do
                    {
                        lastTerrainDistance = terrainDistance;
                        yield return new WaitForFixedUpdate();
                        terrainDistance = Physics.Raycast(ray, out hit, 2f * (float)altitude + 10000f, 1 << 15) ? hit.distance : -1f; // Oceans shouldn't be more than 10km deep...
                        if (terrainDistance < 0f || Math.Abs(lastTerrainDistance - terrainDistance) > 0.1f)
                            lastStableTimeStart = Planetarium.GetUniversalTime(); // Reset the stable time tracker.
                        stableTime = Planetarium.GetUniversalTime() - lastStableTimeStart;
                    } while (Planetarium.GetUniversalTime() - startTime < 10 && stableTime < 1f);
                    if (terrainDistance < 0)
                    {
                        if (!spawnAirborne)
                        {
                            message = "Failed to find terrain at the spawning point!";
                            Debug.Log("[VesselSpawner]: " + message);
                            BDACompetitionMode.Instance.competitionStatus.Add(message);
                            vesselsSpawning = false;
                            spawnFailureReason = SpawnFailureReason.NoTerrain;
                            yield break;
                        }
                    }
                    else
                    {
                        spawnPoint = hit.point + (float)altitude * hit.normal;
                        localSurfaceNormal = hit.normal;
                    }
                }
            }
            else if ((spawnPoint - FloatingOrigin.fetch.offset).magnitude > 100e3)
            {
                message = "WARNING The spawn point is " + ((spawnPoint - FloatingOrigin.fetch.offset).magnitude / 1000).ToString("G4") + "km away. Expect vessels to be killed immediately.";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
            }

            // Spawn the craft in an outward facing ring.
            Debug.Log("[VesselSpawner]: Spawning vessels...");
            var spawnedVessels = new Dictionary<string, Tuple<Vessel, Vector3d, Vector3, float, EditorFacility>>();
            Vector3d craftGeoCoords;
            Vector3 craftSpawnPosition;
            var refDirection = Math.Abs(Vector3.Dot(Vector3.up, localSurfaceNormal)) < 0.9f ? Vector3.up : Vector3.forward; // Avoid that the reference direction is colinear with the local surface normal.
            string failedVessels = "";
            var shipFacility = EditorFacility.None;
            foreach (var craftUrl in crafts) // First spawn the vessels in the air.
            {
                var heading = 360f * spawnedVesselCount / crafts.Count;
                var direction = Vector3.ProjectOnPlane(Quaternion.AngleAxis(heading, localSurfaceNormal) * refDirection, localSurfaceNormal).normalized;
                var spawnDistance = crafts.Count > 1 ? (spawnDistanceFactor + spawnDistanceFactor * crafts.Count) : 0f; // If it's a single craft, spawn it at the spawn point.
                craftSpawnPosition = 1000f * localSurfaceNormal + spawnDistance * direction; // Spawn 1000m higher than asked for, then adjust the altitude later once the craft's loaded.
                FlightGlobals.currentMainBody.GetLatLonAlt(craftSpawnPosition, out craftGeoCoords.x, out craftGeoCoords.y, out craftGeoCoords.z); // Convert spawn point to geo-coords for the actual spawning function.
                Vessel vessel = null;
                try
                {
                    vessel = SpawnVesselFromCraftFile(craftUrl, craftGeoCoords, 0, 0f, out shipFacility); // SPAWN
                }
                catch { vessel = null; }
                if (vessel == null)
                {
                    var craftName = craftUrl.Substring((Environment.CurrentDirectory + $"/AutoSpawn/{spawnFolder}").Length);
                    Debug.Log("[VesselSpawner]: Failed to spawn craft " + craftName);
                    failedVessels += "\n  -  " + craftName;
                    continue;
                }
                vessel.Landed = false; // Tell KSP that it's not landed so KSP doesn't mess with its position.
                vessel.ResumeStaging(); // Trigger staging to resume to get staging icons to work properly.
                if (spawnedVessels.ContainsKey(vessel.GetName()))
                    vessel.vesselName += "_" + spawnedVesselCount;
                spawnedVessels.Add(vessel.GetName(), new Tuple<Vessel, Vector3d, Vector3, float, EditorFacility>(vessel, craftSpawnPosition, direction, vessel.GetHeightFromTerrain() - 35f, shipFacility)); // Store the vessel, its spawning point (which is different from its position) and height from the terrain!
                ++spawnedVesselCount;
            }
            if (failedVessels != "")
            {
                message += "Some vessels failed to spawn: " + failedVessels;
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.Log("[VesselSpawner]: " + message);
            }

            // Wait for an update so that the vessels' parts list gets updated.
            yield return new WaitForFixedUpdate();

            // Count the vessels' parts for checking later.
            var spawnedVesselPartCounts = new Dictionary<string, int>();
            foreach (var vesselName in spawnedVessels.Keys)
                spawnedVesselPartCounts.Add(vesselName, spawnedVessels[vesselName].Item1.parts.Count);

            // Now rotate them and put them at the right altitude.
            foreach (var vesselName in spawnedVessels.Keys)
            {
                var vessel = spawnedVessels[vesselName].Item1;
                craftSpawnPosition = spawnedVessels[vesselName].Item2;
                var direction = spawnedVessels[vesselName].Item3;
                var heightFromTerrain = spawnedVessels[vesselName].Item4;
                shipFacility = spawnedVessels[vesselName].Item5;
                ray = new Ray(craftSpawnPosition, -localSurfaceNormal);
                var distance = Physics.Raycast(ray, out hit, (float)(altitude + 1100f), 1 << 15) ? hit.distance : (float)altitude + 1100f; // Note: if this doesn't hit, then the terrain is too steep to spawn on anyway.
                if (!spawnAirborne)
                {
                    vessel.SetRotation(Quaternion.FromToRotation(shipFacility == EditorFacility.SPH ? -Vector3.forward : Vector3.up, localSurfaceNormal)); // Re-orient the vessel to the terrain normal.
                    vessel.SetRotation(Quaternion.AngleAxis(Vector3.SignedAngle(shipFacility == EditorFacility.SPH ? vessel.transform.up : -vessel.transform.forward, direction, localSurfaceNormal), localSurfaceNormal) * vessel.transform.rotation); // Re-orient the vessel to the right direction.
                }
                else
                {
                    var normal = -FlightGlobals.getGeeForceAtPosition(craftSpawnPosition); //terrainAltitude > 0 ? hit.normal : (Vector3)localSurfaceNormal; // Use water or terrain normal, whichever is on top.
                    vessel.SetRotation(Quaternion.FromToRotation(-Vector3.up, normal)); // Re-orient the vessel to point at the terrain normal.
                    vessel.SetRotation(Quaternion.AngleAxis(Vector3.SignedAngle(-vessel.transform.forward, direction, normal), normal) * vessel.transform.rotation); // Re-orient the vessel to the right direction.
                }
                if (FlightGlobals.currentMainBody.hasSolidSurface)
                    vessel.SetPosition(craftSpawnPosition + localSurfaceNormal * (altitude + heightFromTerrain - distance)); // Put us at the specified altitude. Vessel rootpart height gets 35 added to it during spawning. We can't use vesselSize.y/2 as 'position' is not central to the vessel.
                else
                    vessel.SetPosition(craftSpawnPosition + -1000f * localSurfaceNormal);
                if (vessel.mainBody.ocean) // Check for being under water.
                {
                    var distanceUnderWater = (float)(distance * Vector3.Dot(surfaceNormal, localSurfaceNormal) - vessel.altitude);
                    if (distanceUnderWater > 0) // Under water, move the vessel to the surface.
                        vessel.SetPosition(vessel.transform.position + distanceUnderWater * surfaceNormal);
                }
                ray = new Ray(vessel.transform.position, -localSurfaceNormal);
                Debug.Log("[VesselSpawner]: Vessel " + vessel.vesselName + " spawned!");
            }
            #endregion

            #region Post-spawning
            yield return new WaitForFixedUpdate();
            var postSpawnCheckStartTime = Planetarium.GetUniversalTime();
            var allWeaponManagersAssigned = false;
            var vesselsToCheck = spawnedVessels.Select(v => v.Value.Item1).ToList();
            if (vesselsToCheck.Count > 0)
            {
                List<Tuple<string, BDACompetitionMode.InvalidVesselReason>> invalidVessels;
                // Check that the spawned vessels are valid craft
                do
                {
                    yield return new WaitForFixedUpdate();
                    invalidVessels = vesselsToCheck.Select(vessel => new Tuple<string, BDACompetitionMode.InvalidVesselReason>(vessel.vesselName, BDACompetitionMode.Instance.IsValidVessel(vessel))).Where(t => t.Item2 != BDACompetitionMode.InvalidVesselReason.None).ToList();
                } while (invalidVessels.Count > 0 && Planetarium.GetUniversalTime() - postSpawnCheckStartTime < 1); // Give it up to 1s for KSP to populate the vessel's AI and WM.
                if (invalidVessels.Count > 0)
                {
                    BDACompetitionMode.Instance.competitionStatus.Add("The following vessels are invalid:\n - " + string.Join("\n - ", invalidVessels.Select(t => t.Item1 + " : " + t.Item2)));
                    Debug.Log("[VesselSpawner]: Invalid vessels: " + string.Join(", ", invalidVessels.Select(t => t.Item1 + ":" + t.Item2)));
                    spawnFailureReason = SpawnFailureReason.InvalidVessel;
                }
                else
                {
                    do
                    {
                        yield return new WaitForFixedUpdate();

                        // Check that none of the vessels have lost parts.
                        if (spawnedVessels.Any(kvp => kvp.Value.Item1.parts.Count < spawnedVesselPartCounts[kvp.Key]))
                        {
                            message = "One of the vessel lost parts after spawning.";
                            BDACompetitionMode.Instance.competitionStatus.Add(message);
                            Debug.Log("[VesselSpawner]: " + message);
                            spawnFailureReason = SpawnFailureReason.VesselLostParts;
                            break;
                        }

                        // Wait for all the weapon managers to be added to LoadedVesselSwitcher.
                        LoadedVesselSwitcher.Instance.UpdateList();
                        var weaponManagers = LoadedVesselSwitcher.Instance.weaponManagers.SelectMany(tm => tm.Value).ToList();
                        foreach (var vessel in vesselsToCheck.ToList())
                        {
                            var weaponManager = vessel.FindPartModuleImplementing<MissileFire>();
                            if (weaponManager != null && weaponManagers.Contains(weaponManager)) // The weapon manager has been added, let's go!
                            {
                                // Turn on the brakes.
                                spawnedVessels[vessel.GetName()].Item1.ActionGroups.SetGroup(KSPActionGroup.Brakes, false); // Disable them first to make sure they trigger on toggling.
                                spawnedVessels[vessel.GetName()].Item1.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);

                                vesselsToCheck.Remove(vessel);
                            }
                        }
                        if (vesselsToCheck.Count == 0)
                            allWeaponManagersAssigned = true;

                        if (allWeaponManagersAssigned)
                            break;
                    } while (Planetarium.GetUniversalTime() - postSpawnCheckStartTime < 10); // Give it up to 10s for the weapon managers to get added to the LoadedVesselSwitcher's list.
                    if (!allWeaponManagersAssigned)
                        spawnFailureReason = SpawnFailureReason.TimedOut;
                }
            }

            if (allWeaponManagersAssigned)
            {
                if (!spawnAirborne)
                {
                    // Prevent the vessels from falling too fast and check if their velocities in the surface normal direction is below a threshold.
                    var vesselsHaveLanded = spawnedVessels.Keys.ToDictionary(v => v, v => (int)0); // 1=started moving, 2=landed.
                    var landingStartTime = Planetarium.GetUniversalTime();
                    do
                    {
                        yield return new WaitForFixedUpdate();
                        foreach (var vesselName in spawnedVessels.Keys)
                        {
                            if (vesselsHaveLanded[vesselName] == 0 && Vector3.Dot(spawnedVessels[vesselName].Item1.srf_velocity, localSurfaceNormal) < 0) // Check that vessel has started moving.
                                vesselsHaveLanded[vesselName] = 1;
                            if (vesselsHaveLanded[vesselName] == 1 && Vector3.Dot(spawnedVessels[vesselName].Item1.srf_velocity, localSurfaceNormal) >= 0) // Check if the vessel has landed.
                            {
                                vesselsHaveLanded[vesselName] = 2;
                                spawnedVessels[vesselName].Item1.Landed = true; // Tell KSP that the vessel is landed.
                            }
                            if (vesselsHaveLanded[vesselName] == 1 && spawnedVessels[vesselName].Item1.srf_velocity.sqrMagnitude > easeInSpeed) // While the vessel hasn't landed, prevent it from moving too fast.
                                spawnedVessels[vesselName].Item1.SetWorldVelocity(0.99 * easeInSpeed * spawnedVessels[vesselName].Item1.srf_velocity); // Move at VESSEL_SPAWN_EASE_IN_SPEED m/s at most.
                        }

                        // Check that none of the vessels have lost parts.
                        if (spawnedVessels.Any(kvp => kvp.Value.Item1.parts.Count < spawnedVesselPartCounts[kvp.Key]))
                        {
                            message = "One of the vessel lost parts after spawning.";
                            BDACompetitionMode.Instance.competitionStatus.Add(message);
                            spawnFailureReason = SpawnFailureReason.VesselLostParts;
                            break;
                        }

                        if (vesselsHaveLanded.Values.All(v => v == 2))
                        {
                            vesselSpawnSuccess = true;
                            message = "Vessel spawning SUCCEEDED!";
                            BDACompetitionMode.Instance.competitionStatus.Add(message);
                            break;
                        }
                    } while (Planetarium.GetUniversalTime() - landingStartTime < 5 + altitude / easeInSpeed); // Give the vessels up to (5 + altitude / VESSEL_SPAWN_EASE_IN_SPEED) seconds to land.
                }
                else
                {
                    // Check that none of the vessels have lost parts.
                    if (spawnedVessels.Any(kvp => kvp.Value.Item1.parts.Count < spawnedVesselPartCounts[kvp.Key]))
                    {
                        message = "One of the vessel lost parts after spawning.";
                        BDACompetitionMode.Instance.competitionStatus.Add(message);
                        spawnFailureReason = SpawnFailureReason.VesselLostParts;
                    }
                    else
                    {
                        foreach (var vessel in spawnedVessels.Select(v => v.Value.Item1))
                        {
                            var weaponManager = vessel.FindPartModuleImplementing<MissileFire>();
                            if (!weaponManager) continue; // Safety check in case the vessel got destroyed.

                            // Activate the vessel with AG10, or failing that, staging.
                            vessel.ActionGroups.ToggleGroup(BDACompetitionMode.KM_dictAG[10]); // Modular Missiles use lower AGs (1-3) for staging, use a high AG number to not affect them
                            weaponManager.AI.ActivatePilot();
                            weaponManager.AI.CommandTakeOff();
                            if (!vessel.FindPartModulesImplementing<ModuleEngines>().Any(engine => engine.EngineIgnited)) // If the vessel didn't activate their engines on AG10, then activate all their engines and hope for the best.
                            {
                                // Debug.Log("[VesselSpawner]: Firing next stage for " + vessel.GetName() + " as they forgot to add engines to AG10!");
                                // BDArmory.Misc.Misc.fireNextNonEmptyStage(vessel);
                                Debug.Log("[VesselSpawner]: " + vessel.GetName() + " didn't activate engines on AG10! Activating ALL their engines.");
                                foreach (var engine in vessel.FindPartModulesImplementing<ModuleEngines>())
                                    engine.Activate();
                            }
                            vessel.altimeterDisplayState = AltimeterDisplayState.AGL;
                        }

                        vesselSpawnSuccess = true;
                    }
                }
            }
            if (!vesselSpawnSuccess)
            {
                message = "Vessel spawning FAILED!";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
            }
            else
            {
                // Assign the vessels to their own teams.
                LoadedVesselSwitcher.MassTeamSwitch(true);
                LoadedVesselSwitcher.Instance.ForceSwitchVessel(spawnedVessels.First().Value.Item1); // Update the camera.
                yield return new WaitForFixedUpdate();
            }
            #endregion

            Debug.Log("[VesselSpawner]: Vessel spawning " + (vesselSpawnSuccess ? "SUCCEEDED!" : "FAILED!"));
            vesselsSpawning = false;
        }


        // For tracking scores across multiple spawns.
        public class ContinuousSpawningScores
        {
            public Vessel vessel; // The vessel.
            public int spawnCount = 0; // The number of times a craft has been spawned.
            public double outOfAmmoTime = 0; // The time the vessel ran out of ammo.
            public Dictionary<int, ScoringData> scoreData = new Dictionary<int, ScoringData>();
            public Dictionary<int, string> cleanKilledBy = new Dictionary<int, string>();
            public Dictionary<int, string> cleanRammedBy = new Dictionary<int, string>();
            public Dictionary<int, string> cleanMissileKilledBy = new Dictionary<int, string>();
            public double cumulativeTagTime = 0;
        };
        public Dictionary<string, ContinuousSpawningScores> continuousSpawningScores;
        public void UpdateCompetitionScores(Vessel vessel, bool newSpawn = false)
        {
            var vesselName = vessel.GetName();
            if (!continuousSpawningScores.ContainsKey(vesselName)) return;
            var spawnCount = continuousSpawningScores[vesselName].spawnCount - 1;
            if (spawnCount < 0) return; // Initial spawning after scores were reset.
            var scoreData = continuousSpawningScores[vesselName].scoreData;
            if (BDACompetitionMode.Instance.DeathOrder.ContainsKey(vesselName))
                BDACompetitionMode.Instance.DeathOrder.Remove(vesselName);
            if (BDACompetitionMode.Instance.Scores.ContainsKey(vesselName))
            {
                scoreData[spawnCount] = BDACompetitionMode.Instance.Scores[vesselName]; // Save the Score instance for the vessel.
                if (newSpawn)
                {
                    BDACompetitionMode.Instance.Scores[vesselName] = new ScoringData { lastFiredTime = Planetarium.GetUniversalTime(), previousPartCount = vessel.parts.Count(), tagIsIt = scoreData[spawnCount].tagIsIt };
                    continuousSpawningScores[vesselName].cumulativeTagTime = scoreData.Sum(kvp => kvp.Value.tagTotalTime);
                    // Re-insert some information needed for Tag.
                    switch (scoreData[spawnCount].LastDamageWasFrom())
                    {
                        case DamageFrom.Bullet:
                            BDACompetitionMode.Instance.Scores[vesselName].lastHitTime = scoreData[spawnCount].lastHitTime;
                            BDACompetitionMode.Instance.Scores[vesselName].lastPersonWhoHitMe = scoreData[spawnCount].lastPersonWhoHitMe;
                            break;
                        case DamageFrom.Ram:
                            BDACompetitionMode.Instance.Scores[vesselName].lastRammedTime = scoreData[spawnCount].lastRammedTime;
                            BDACompetitionMode.Instance.Scores[vesselName].lastPersonWhoRammedMe = scoreData[spawnCount].lastPersonWhoRammedMe;
                            break;
                        case DamageFrom.Missile:
                            BDACompetitionMode.Instance.Scores[vesselName].lastMissileHitTime = scoreData[spawnCount].lastMissileHitTime;
                            BDACompetitionMode.Instance.Scores[vesselName].lastPersonWhoHitMeWithAMissile = scoreData[spawnCount].lastPersonWhoHitMeWithAMissile;
                            break;
                        default:
                            break;
                    }
                }
            }
            if (BDACompetitionMode.Instance.whoCleanShotWho.ContainsKey(vesselName))
            {
                continuousSpawningScores[vesselName].cleanKilledBy[spawnCount] = BDACompetitionMode.Instance.whoCleanShotWho[vesselName];
                if (newSpawn) BDACompetitionMode.Instance.whoCleanShotWho.Remove(vesselName);
            }
            if (BDACompetitionMode.Instance.whoCleanRammedWho.ContainsKey(vesselName))
            {
                continuousSpawningScores[vesselName].cleanRammedBy[spawnCount] = BDACompetitionMode.Instance.whoCleanRammedWho[vesselName];
                if (newSpawn) BDACompetitionMode.Instance.whoCleanRammedWho.Remove(vesselName);
            }
            if (BDACompetitionMode.Instance.whoCleanShotWhoWithMissiles.ContainsKey(vesselName))
            {
                continuousSpawningScores[vesselName].cleanMissileKilledBy[spawnCount] = BDACompetitionMode.Instance.whoCleanShotWhoWithMissiles[vesselName];
                if (newSpawn) BDACompetitionMode.Instance.whoCleanShotWhoWithMissiles.Remove(vesselName);
            }
        }

        public void DumpContinuousSpawningScores(string tag = "")
        {
            var logStrings = new List<string>();

            if (continuousSpawningScores == null || continuousSpawningScores.Count == 0) return;
            foreach (var vesselName in continuousSpawningScores.Keys)
                UpdateCompetitionScores(continuousSpawningScores[vesselName].vessel);
            logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]: Dumping Results");
            foreach (var vesselName in continuousSpawningScores.Keys)
            {
                var vesselScore = continuousSpawningScores[vesselName];
                var scoreData = vesselScore.scoreData;
                logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]: Name:" + vesselName);
                logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  DEATHCOUNT:" + (vesselScore.spawnCount - 1 + (vesselsToActivate.Contains(vesselScore.vessel) ? 1 : 0))); // Account for vessels that haven't respawned yet.
                var whoShotMeScores = string.Join(", ", scoreData.Where(kvp => kvp.Value.hitCounts.Count > 0).Select(kvp => kvp.Key + ":" + string.Join(";", kvp.Value.hitCounts.Select(kvp2 => kvp2.Value + ":" + kvp2.Key))));
                if (whoShotMeScores != "") logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  WHOSHOTME:" + whoShotMeScores);
                var whoDamagedMeWithBulletsScores = string.Join(", ", scoreData.Where(kvp => kvp.Value.damageFromBullets.Count > 0).Select(kvp => kvp.Key + ":" + string.Join(";", kvp.Value.damageFromBullets.Select(kvp2 => kvp2.Value.ToString("0.0") + ":" + kvp2.Key))));
                if (whoDamagedMeWithBulletsScores != "") logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  WHODAMAGEDMEWITHBULLETS:" + whoDamagedMeWithBulletsScores);
                var whoRammedMeScores = string.Join(", ", scoreData.Where(kvp => kvp.Value.rammingPartLossCounts.Count > 0).Select(kvp => kvp.Key + ":" + string.Join(";", kvp.Value.rammingPartLossCounts.Select(kvp2 => kvp2.Value + ":" + kvp2.Key))));
                if (whoRammedMeScores != "") logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  WHORAMMEDME:" + whoRammedMeScores);
                var whoShotMeWithMissilesScores = string.Join(", ", scoreData.Where(kvp => kvp.Value.missilePartDamageCounts.Count > 0).Select(kvp => kvp.Key + ":" + string.Join(";", kvp.Value.missilePartDamageCounts.Select(kvp2 => kvp2.Value + ":" + kvp2.Key))));
                if (whoShotMeWithMissilesScores != "") logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  WHOSHOTMEWITHMISSILES:" + whoShotMeWithMissilesScores);
                var whoDamagedMeWithMissilesScores = string.Join(", ", scoreData.Where(kvp => kvp.Value.damageFromMissiles.Count > 0).Select(kvp => kvp.Key + ":" + string.Join(";", kvp.Value.damageFromMissiles.Select(kvp2 => kvp2.Value.ToString("0.0") + ":" + kvp2.Key))));
                if (whoDamagedMeWithMissilesScores != "") logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  WHODAMAGEDMEWITHMISSILES:" + whoDamagedMeWithMissilesScores);
                var otherKills = string.Join(", ", scoreData.Where(kvp => kvp.Value.gmKillReason != GMKillReason.None).Select(kvp => kvp.Key + ":" + kvp.Value.gmKillReason));
                if (otherKills != "") logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  OTHERKILL:" + otherKills);
                if (vesselScore.cleanKilledBy.Count > 0) logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  CLEANKILL:" + string.Join(", ", vesselScore.cleanKilledBy.Select(kvp => kvp.Key + ":" + kvp.Value)));
                if (vesselScore.cleanRammedBy.Count > 0) logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  CLEANRAM:" + string.Join(", ", vesselScore.cleanRammedBy.Select(kvp => kvp.Key + ":" + kvp.Value)));
                if (vesselScore.cleanMissileKilledBy.Count > 0) logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  CLEANMISSILEKILL:" + string.Join(", ", vesselScore.cleanMissileKilledBy.Select(kvp => kvp.Key + ":" + kvp.Value)));
                if (scoreData.Sum(kvp => kvp.Value.shotsFired) > 0) logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  ACCURACY:" + string.Join(", ", scoreData.Where(kvp => kvp.Value.shotsFired > 0).Select(kvp => kvp.Key + ":" + kvp.Value.Score + "/" + kvp.Value.shotsFired)));
                if (BDArmorySettings.TAG_MODE)
                {
                    if (scoreData.Sum(kvp => kvp.Value.tagScore) > 0) logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  TAGSCORE:" + string.Join(", ", scoreData.Where(kvp => kvp.Value.tagScore > 0).Select(kvp => kvp.Key + ":" + kvp.Value.tagScore.ToString("0.0"))));
                    if (scoreData.Sum(kvp => kvp.Value.tagTotalTime) > 0) logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  TIMEIT:" + string.Join(", ", scoreData.Where(kvp => kvp.Value.tagTotalTime > 0).Select(kvp => kvp.Key + ":" + kvp.Value.tagTotalTime.ToString("0.0"))));
                    if (scoreData.Sum(kvp => kvp.Value.tagKillsWhileIt) > 0) logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  KILLSWHILEIT:" + string.Join(", ", scoreData.Where(kvp => kvp.Value.tagKillsWhileIt > 0).Select(kvp => kvp.Key + ":" + kvp.Value.tagKillsWhileIt)));
                    if (scoreData.Sum(kvp => kvp.Value.tagTimesIt) > 0) logStrings.Add("[VesselSpawner:" + BDACompetitionMode.Instance.CompetitionID + "]:  TIMESIT:" + string.Join(", ", scoreData.Where(kvp => kvp.Value.tagTimesIt > 0).Select(kvp => kvp.Key + ":" + kvp.Value.tagTimesIt)));
                }
            }

            // Dump the log results to a file.
            if (BDACompetitionMode.Instance.CompetitionID > 0)
            {
                var folder = Environment.CurrentDirectory + "/GameData/BDArmory/Logs";
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
                File.WriteAllLines(Path.Combine(folder, BDACompetitionMode.Instance.CompetitionID.ToString() + (tag != "" ? "-" + tag : "") + ".log"), logStrings);
            }
            // Also dump the results to the normal log.
            foreach (var line in logStrings)
                Debug.Log(line);
        }

        public bool vesselsSpawningContinuously = false;
        int continuousSpawnedVesselCount = 0;
        public void SpawnVesselsContinuously(Vector2d geoCoords, double altitude = 1000, float spawnDistanceFactor = 20f, bool killEverythingFirst = true, string spawnFolder = null)
        {
            vesselsSpawningContinuously = true;
            spawnFailureReason = SpawnFailureReason.None;
            continuousSpawningScores = new Dictionary<string, ContinuousSpawningScores>();
            if (spawnVesselsContinuouslyCoroutine != null)
                StopCoroutine(spawnVesselsContinuouslyCoroutine);
            spawnVesselsContinuouslyCoroutine = StartCoroutine(SpawnVesselsContinuouslyCoroutine(geoCoords, altitude, spawnDistanceFactor, killEverythingFirst, spawnFolder));
            Debug.Log("[VesselSpawner]: Triggering continuous vessel spawning at " + BDArmorySettings.VESSEL_SPAWN_GEOCOORDS.ToString("G6") + " at altitude " + altitude + "m.");
        }
        private Coroutine spawnVesselsContinuouslyCoroutine;
        HashSet<Vessel> vesselsToActivate = new HashSet<Vessel>();
        // Spawns all vessels in a downward facing ring and activates them (autopilot and AG10, then stage if no engines are firing), then respawns any that die. An altitude of 1000m should be plenty.
        // Note: initial vessel separation tends towards 2*pi*spawnDistanceFactor from above for >3 vessels.
        private IEnumerator SpawnVesselsContinuouslyCoroutine(Vector2d geoCoords, double altitude, float spawnDistanceFactor, bool killEverythingFirst, string spawnFolder = null)
        {
            #region Initialisation and sanity checks
            // Tally up the craft to spawn.
            var crafts = Directory.GetFiles(Environment.CurrentDirectory + $"/AutoSpawn/{spawnFolder}").Where(f => f.EndsWith(".craft")).ToList();
            if (crafts.Count == 0)
            {
                message = "Vessel spawning: found no craft files in " + Environment.CurrentDirectory + $"/AutoSpawn/{spawnFolder}";
                Debug.Log("[VesselSpawner]: " + message);
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                vesselsSpawning = false;
                spawnFailureReason = SpawnFailureReason.NoCraft;
                yield break;
            }
            crafts.Shuffle(); // Randomise the spawn order.
            altitude = Math.Max(100, altitude); // Don't spawn too low.
            continuousSpawnedVesselCount = 0; // Reset our spawned vessel count.
            message = "Spawning " + crafts.Count + " vessels at an altitude of " + altitude.ToString("G0") + (crafts.Count > 8 ? ", this may take some time..." : ".");
            Debug.Log("[VesselSpawner]: " + message);
            if (BDACompetitionMode.Instance) // Reset competition stuff.
            {
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                BDACompetitionMode.Instance.LogResults("due to continuous spawning.", "auto-dump-from-spawning"); // Log results first.
                BDACompetitionMode.Instance.StopCompetition();
                BDACompetitionMode.Instance.ResetCompetitionScores(); // Reset competition scores.
            }
            yield return new WaitForFixedUpdate();
            #endregion

            #region Pre-spawning
            if (killEverythingFirst)
            {
                // Kill all vessels (including debris).
                var vesselsToKill = new List<Vessel>(FlightGlobals.Vessels);
                foreach (var vessel in vesselsToKill)
                    RemoveVessel(vessel);
            }
            while (removeVesselsPending > 0)
                yield return new WaitForFixedUpdate();
            #endregion

            #region Spawning
            // Get the spawning point in world position coordinates.
            var terrainAltitude = FlightGlobals.currentMainBody.TerrainAltitude(geoCoords.x, geoCoords.y);
            var spawnPoint = FlightGlobals.currentMainBody.GetWorldSurfacePosition(geoCoords.x, geoCoords.y, terrainAltitude);
            var surfaceNormal = FlightGlobals.currentMainBody.hasSolidSurface ? FlightGlobals.currentMainBody.GetSurfaceNVector(geoCoords.x, geoCoords.y) : Vector3d.up;
            if (FlightGlobals.currentMainBody.ocean && FlightGlobals.currentMainBody.GetAltitude(spawnPoint) > terrainAltitude) // Adjust for oceans.
                spawnPoint += surfaceNormal * (FlightGlobals.currentMainBody.GetAltitude(spawnPoint) - terrainAltitude);
            Ray ray;
            RaycastHit hit;

            if (killEverythingFirst)
            {
                // Update the floating origin offset, so that the vessels spawn within range of the physics. Unfortunately, the terrain takes several frames to load, so the first spawn in this region is often below the terrain level.
                FloatingOrigin.SetOffset(spawnPoint); // This adjusts local coordinates, such that spawnPoint is (0,0,0).
                var flightCamera = FlightCamera.fetch;
                flightCamera.SetCamCoordsFromPosition(spawnPoint);
                // flightCamera.transform.position = altitude * surfaceNormal;
                // flightCamera.transform.position = Vector3d.zero;
                // flightCamera.cameraAlt = (float)(altitude + terrainAltitude);
                flightCamera.transform.rotation = Quaternion.FromToRotation(flightCamera.transform.up, -surfaceNormal) * flightCamera.transform.rotation;
                yield return new WaitForFixedUpdate(); // Give it a moment to start loading in terrain.

                if (FlightGlobals.currentMainBody.hasSolidSurface)
                {
                    // Wait for the terrain to load in before continuing.
                    var testPosition = altitude * surfaceNormal;
                    var terrainDistance = 2f * (float)altitude;
                    var lastTerrainDistance = terrainDistance;
                    ray = new Ray(testPosition, -surfaceNormal);
                    message = "Waiting up to 10s for terrain to settle.";
                    Debug.Log("[VesselSpawner]: " + message);
                    BDACompetitionMode.Instance.competitionStatus.Add(message);
                    var startTime = Planetarium.GetUniversalTime();
                    double lastStableTimeStart = startTime;
                    double stableTime = 0;
                    do
                    {
                        lastTerrainDistance = terrainDistance;
                        yield return new WaitForFixedUpdate();
                        terrainDistance = Physics.Raycast(ray, out hit, 2f * (float)altitude + 10000f, 1 << 15) ? hit.distance : -1f; // Oceans shouldn't be more than 10km deep...
                        if (terrainDistance < 0f || Math.Abs(lastTerrainDistance - terrainDistance) > 0.1f)
                            lastStableTimeStart = Planetarium.GetUniversalTime(); // Reset the stable time tracker.
                        stableTime = Planetarium.GetUniversalTime() - lastStableTimeStart;
                    } while (Planetarium.GetUniversalTime() - startTime < 10 && stableTime < 1f);
                }
            }
            else if ((spawnPoint - FloatingOrigin.fetch.offset).magnitude > 100e3)
            {
                message = "WARNING The spawn point is " + ((spawnPoint - FloatingOrigin.fetch.offset).magnitude / 1000).ToString("G4") + "km away. Expect vessels to be killed immediately.";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
            }

            var craftURLToVesselName = new Dictionary<string, string>();
            var activeWeaponManagersByCraftURL = new Dictionary<string, MissileFire>();
            var invalidVesselCount = new Dictionary<string, int>();
            Vector3d craftGeoCoords;
            Vector3 craftSpawnPosition;
            var shipFacility = EditorFacility.None;
            var refDirection = Math.Abs(Vector3.Dot(Vector3.up, surfaceNormal)) < 0.9f ? Vector3.up : Vector3.forward; // Avoid that the reference direction is colinear with the local surface normal.
            var spawnSlots = OptimiseSpawnSlots(crafts.Count);
            while (vesselsSpawningContinuously)
            {
                // Reacquire the spawn point as the local coordinate system may have changed (floating origin adjustments, local body rotation, etc.).
                spawnPoint = FlightGlobals.currentMainBody.GetWorldSurfacePosition(geoCoords.x, geoCoords.y, terrainAltitude);
                surfaceNormal = FlightGlobals.currentMainBody.GetSurfaceNVector(geoCoords.x, geoCoords.y);
                if (FlightGlobals.currentMainBody.ocean && FlightGlobals.currentMainBody.GetAltitude(spawnPoint) > terrainAltitude) // Adjust for oceans.
                    spawnPoint += surfaceNormal * (FlightGlobals.currentMainBody.GetAltitude(spawnPoint) - terrainAltitude);
                var craftToSpawn = crafts.Where(craftURL => !craftURLToVesselName.ContainsKey(craftURL) || (activeWeaponManagersByCraftURL.ContainsKey(craftURL) && activeWeaponManagersByCraftURL[craftURL] == null)).ToList(); // Vessels that haven't been spawned yet, or have died.
                if (craftToSpawn.Count > 0)
                {
                    // Spawn the craft in a downward facing ring.
                    string failedVessels = "";
                    foreach (var craftURL in craftToSpawn)
                    {
                        if (activeWeaponManagersByCraftURL.ContainsKey(craftURL))
                            activeWeaponManagersByCraftURL.Remove(craftURL);
                        var heading = 360f * spawnSlots[continuousSpawnedVesselCount] / crafts.Count;
                        var direction = Vector3.ProjectOnPlane(Quaternion.AngleAxis(heading, surfaceNormal) * refDirection, surfaceNormal).normalized;
                        var spawnDistance = crafts.Count > 1 ? spawnDistanceFactor + spawnDistanceFactor * crafts.Count : 0f; // If it's a single craft, spawn it at the spawn point.
                        craftSpawnPosition = spawnPoint + altitude * (Vector3d)surfaceNormal + spawnDistance * direction;
                        FlightGlobals.currentMainBody.GetLatLonAlt(craftSpawnPosition, out craftGeoCoords.x, out craftGeoCoords.y, out craftGeoCoords.z); // Convert spawn point to geo-coords for the actual spawning function.
                        Vessel vessel = null;
                        try
                        {
                            vessel = SpawnVesselFromCraftFile(craftURL, craftGeoCoords, 0, 0f, out shipFacility); // SPAWN
                        }
                        catch { vessel = null; }
                        if (vessel == null)
                        {
                            var craftName = craftURL.Substring((Environment.CurrentDirectory + $"/AutoSpawn/{spawnFolder}").Length);
                            Debug.Log("[VesselSpawner]: Failed to spawn craft " + craftName);
                            failedVessels += "\n  -  " + craftName;
                            continue;
                        }
                        vessel.Landed = false; // Tell KSP that it's not landed.
                        vessel.ResumeStaging(); // Trigger staging to resume to get staging icons to work properly.
                        vessel.SetRotation(Quaternion.FromToRotation(-Vector3.up, surfaceNormal)); // Re-orient the vessel to the terrain normal.
                        vessel.SetRotation(Quaternion.AngleAxis(Vector3.SignedAngle(-vessel.transform.forward, direction, surfaceNormal), surfaceNormal) * vessel.transform.rotation); // Re-orient the vessel to the right direction.
                        if (!craftURLToVesselName.ContainsKey(craftURL))
                        {
                            if (craftURLToVesselName.ContainsValue(vessel.GetName())) // Avoid duplicate names.
                                vessel.vesselName += "_" + continuousSpawnedVesselCount; // This shouldn't give duplicates as there shouldn't be more than crafts.Count craft.
                            craftURLToVesselName.Add(craftURL, vessel.GetName()); // Store the craftURL -> vessel name.
                        }
                        vessel.vesselName = craftURLToVesselName[craftURL]; // Assign the same (potentially modified) name to the craft each time.
                        if (!vesselsToActivate.Contains(vessel))
                            vesselsToActivate.Add(vessel);
                        if (!continuousSpawningScores.ContainsKey(vessel.GetName()))
                            continuousSpawningScores.Add(vessel.GetName(), new ContinuousSpawningScores());
                        continuousSpawningScores[vessel.GetName()].vessel = vessel; // Update some values in the scoring structure.
                        continuousSpawningScores[vessel.GetName()].outOfAmmoTime = 0;
                        ++continuousSpawnedVesselCount;
                        continuousSpawnedVesselCount %= crafts.Count;
                        Debug.Log("[VesselSpawner]: Vessel " + vessel.vesselName + " spawned!");
                        BDACompetitionMode.Instance.competitionStatus.Add("Spawned " + vessel.vesselName);
                    }
                    if (failedVessels != "")
                    {
                        message = "Some vessels failed to spawn, aborting: " + failedVessels;
                        Debug.Log("[VesselSpawner]: " + message);
                        BDACompetitionMode.Instance.competitionStatus.Add(message);
                        spawnFailureReason = SpawnFailureReason.VesselFailedToSpawn;
                        break;
                    }
                }
                yield return new WaitForFixedUpdate();
                // Activate the AI and fire up any new weapon managers that appeared.
                if (vesselsToActivate.Count > 0)
                {
                    // Wait for an update so that the spawned vessels' parts list gets updated.
                    yield return new WaitForFixedUpdate();

                    LoadedVesselSwitcher.Instance.UpdateList();
                    var weaponManagers = LoadedVesselSwitcher.Instance.weaponManagers.SelectMany(tm => tm.Value).ToList();
                    var vesselsToCheck = vesselsToActivate.ToList(); // Take a copy to avoid modifying the original while iterating over it.
                    foreach (var vessel in vesselsToCheck)
                    {
                        // Check that the vessel is valid.
                        var invalidReason = BDACompetitionMode.Instance.IsValidVessel(vessel);
                        if (invalidReason != BDACompetitionMode.InvalidVesselReason.None)
                        {
                            bool killIt = false;
                            var craftURL = craftURLToVesselName.ToDictionary(i => i.Value, i => i.Key)[vessel.GetName()];
                            if (invalidVesselCount.ContainsKey(craftURL))
                                ++invalidVesselCount[craftURL];
                            else
                                invalidVesselCount.Add(craftURL, 1);
                            if (invalidVesselCount[craftURL] == 3) // After 3 attempts try spawning it again.
                            {
                                message = vessel.vesselName + " is INVALID due to " + invalidReason + ", attempting to respawn it.";
                                if (activeWeaponManagersByCraftURL.ContainsKey(craftURL)) activeWeaponManagersByCraftURL.Remove(craftURL); // Shouldn't occur, but just to be sure.
                                activeWeaponManagersByCraftURL.Add(craftURL, null); // Indicate to the spawning routine that the craft is effectively dead.
                                killIt = true;
                            }
                            if (invalidVesselCount[craftURL] > 5) // After 3 more attempts, mark it as defunct.
                            {
                                message = vessel.vesselName + " is STILL INVALID due to " + invalidReason + ", removing it.";
                                crafts.Remove(craftURL);
                                killIt = true;
                            }
                            if (killIt)
                            {
                                BDACompetitionMode.Instance.competitionStatus.Add(message);
                                Debug.Log("[VesselSpawner]: " + message);
                                vesselsToActivate.Remove(vessel);
                                RemoveVessel(vessel); // Remove the vessel
                            }
                            continue;
                        }

                        // Check if the weapon manager has been added to the weapon managers list.
                        var weaponManager = vessel.FindPartModuleImplementing<MissileFire>();
                        if (weaponManager != null && weaponManagers.Contains(weaponManager)) // The weapon manager has been added, let's go!
                        {
                            // Activate the vessel with AG10, or failing that, staging.
                            vessel.ActionGroups.ToggleGroup(BDACompetitionMode.KM_dictAG[10]); // Modular Missiles use lower AGs (1-3) for staging, use a high AG number to not affect them
                            weaponManager.AI.ActivatePilot();
                            weaponManager.AI.CommandTakeOff();
                            if (!vessel.FindPartModulesImplementing<ModuleEngines>().Any(engine => engine.EngineIgnited)) // If the vessel didn't activate their engines on AG10, then activate all their engines and hope for the best.
                            {
                                Debug.Log("[VesselSpawner]: " + vessel.GetName() + " didn't activate engines on AG10! Activating ALL their engines.");
                                foreach (var engine in vessel.FindPartModulesImplementing<ModuleEngines>())
                                    engine.Activate();
                            }
                            // Assign the vessel to an unassigned team.
                            var currentTeams = weaponManagers.Where(wm => wm != weaponManager).Select(wm => wm.Team).ToHashSet(); // Current teams, excluding us.
                            char team = 'A';
                            while (currentTeams.Contains(BDTeam.Get(team.ToString())))
                                ++team;
                            weaponManager.SetTeam(BDTeam.Get(team.ToString()));
                            var craftURL = craftURLToVesselName.ToDictionary(i => i.Value, i => i.Key)[vessel.GetName()];
                            if (activeWeaponManagersByCraftURL.ContainsKey(craftURL)) activeWeaponManagersByCraftURL.Remove(craftURL); // Shouldn't occur, but just to be sure.
                            activeWeaponManagersByCraftURL.Add(craftURL, weaponManager);
                            // Enable guard mode if a competition is active.
                            if (BDACompetitionMode.Instance.competitionIsActive)
                                if (!weaponManager.guardMode)
                                    weaponManager.ToggleGuardMode();
                            weaponManager.AI.ReleaseCommand();
                            vessel.altimeterDisplayState = AltimeterDisplayState.AGL;
                            // Adjust BDACompetitionMode's scoring structures.
                            UpdateCompetitionScores(vessel, true);
                            ++continuousSpawningScores[vessel.GetName()].spawnCount;
                            if (invalidVesselCount.ContainsKey(craftURL))// Reset the invalid spawn counter.
                                invalidVesselCount.Remove(craftURL);
                            // Update the ramming information for the new vessel.
                            if (BDACompetitionMode.Instance.rammingInformation != null)
                            {
                                BDACompetitionMode.Instance.rammingInformation[vessel.GetName()].vessel = vessel;
                                BDACompetitionMode.Instance.rammingInformation[vessel.GetName()].partCount = vessel.parts.Count;
                                BDACompetitionMode.Instance.rammingInformation[vessel.GetName()].radius = BDACompetitionMode.GetRadius(vessel);
                                foreach (var vesselName in BDACompetitionMode.Instance.rammingInformation.Keys)
                                {
                                    if (vesselName == vessel.GetName()) continue;
                                    if (BDACompetitionMode.Instance.rammingInformation.ContainsKey(vesselName))
                                    {
                                        BDACompetitionMode.Instance.rammingInformation[vesselName].targetInformation[vessel.GetName()] = new BDACompetitionMode.RammingTargetInformation { vessel = vessel };
                                    }
                                }
                            }
                            vesselsToActivate.Remove(vessel);
                            LoadedVesselSwitcher.Instance.ForceSwitchVessel(vessel); // Update the camera.
                        }
                    }
                }

                // Kill off vessels that are out of ammo for too long if we're in continuous spawning mode and a competition is active.
                if (BDACompetitionMode.Instance.competitionIsActive)
                    KillOffOutOfAmmoVessels();

                // Wait for any pending vessel removals.
                while (removeVesselsPending > 0)
                    yield return new WaitForFixedUpdate();

                yield return new WaitForSeconds(1); // 1s between checks. Nothing much happens if nothing needs spawning.
            }
            #endregion
            vesselsSpawningContinuously = false;
            Debug.Log("[VesselSpawner]: Continuous vessel spawning ended.");
        }

        // Stagger the spawn slots to avoid consecutive craft being launched too close together.
        private static List<int> OptimiseSpawnSlots(int slotCount)
        {
            var availableSlots = Enumerable.Range(0, slotCount).ToList();
            if (slotCount < 4) return availableSlots; // Can't do anything about it for < 4 craft.
            var separation = Mathf.CeilToInt(slotCount / 3f); // Start with approximately 120° separation.
            var pos = 0;
            var optimisedSlots = new List<int>();
            while (optimisedSlots.Count < slotCount)
            {
                while (optimisedSlots.Contains(pos)) { ++pos; pos %= slotCount; }
                optimisedSlots.Add(pos);
                pos += separation;
                pos %= slotCount;
            }
            return optimisedSlots;
        }

        private int removeVesselsPending = 0;
        // Remove a vessel and clean up any remaining parts. This fixes the case where the currently focussed vessel refuses to die properly.
        public void RemoveVessel(Vessel vessel)
        {
            if (vessel == null) return;
            ++removeVesselsPending;
            StartCoroutine(RemoveVesselCoroutine(vessel));
        }
        private IEnumerator RemoveVesselCoroutine(Vessel vessel)
        {
            vessel.Die(); // Kill the vessel
            yield return new WaitForFixedUpdate();
            if (vessel != null)
            {
                var partsToKill = vessel.parts.ToList(); // If it left any parts, kill them. (This occurs when the currently focussed vessel gets killed.)
                foreach (var part in partsToKill)
                    part.Die();
            }
            yield return new WaitForFixedUpdate();
            --removeVesselsPending;
        }

        public void KillOffOutOfAmmoVessels()
        {
            if (BDArmorySettings.OUT_OF_AMMO_KILL_TIME < 0) return; // Never
            var now = Planetarium.GetUniversalTime();
            Vessel vessel;
            MissileFire weaponManager;
            ContinuousSpawningScores score;
            foreach (var vesselName in continuousSpawningScores.Keys)
            {
                score = continuousSpawningScores[vesselName];
                vessel = score.vessel;
                if (vessel == null) continue; // Vessel hasn't been respawned yet.
                weaponManager = vessel.FindPartModuleImplementing<MissileFire>();
                if (weaponManager == null) continue; // Weapon manager hasn't registered yet.
                if (score.outOfAmmoTime == 0 && !weaponManager.HasWeaponsAndAmmo())
                    score.outOfAmmoTime = Planetarium.GetUniversalTime();
                if (score.outOfAmmoTime > 0 && now - score.outOfAmmoTime > BDArmorySettings.OUT_OF_AMMO_KILL_TIME)
                {
                    var m = "Killing off " + vesselName + " as they exceeded the out-of-ammo kill time.";
                    BDACompetitionMode.Instance.competitionStatus.Add(m);
                    Debug.Log("[VesselSpawner]: " + m);
                    if (BDACompetitionMode.Instance.Scores.ContainsKey(vesselName))
                    {
                        BDACompetitionMode.Instance.Scores[vesselName].gmKillReason = GMKillReason.OutOfAmmo; // Indicate that it was us who killed it and remove any "clean" kills.
                        if (BDACompetitionMode.Instance.whoCleanShotWho.ContainsKey(vesselName)) BDACompetitionMode.Instance.whoCleanShotWho.Remove(vesselName);
                        if (BDACompetitionMode.Instance.whoCleanRammedWho.ContainsKey(vesselName)) BDACompetitionMode.Instance.whoCleanRammedWho.Remove(vesselName);
                        if (BDACompetitionMode.Instance.whoCleanShotWhoWithMissiles.ContainsKey(vesselName)) BDACompetitionMode.Instance.whoCleanShotWhoWithMissiles.Remove(vesselName);
                    }
                    RemoveVessel(vessel);
                }
            }
        }

        // THE FOLLOWING STOLEN FROM VESSEL MOVER via BenBenWilde's autospawn (and tweaked slightly)
        private Vessel SpawnVesselFromCraftFile(string craftURL, Vector3d gpsCoords, float heading, float pitch, out EditorFacility shipFacility, List<ProtoCrewMember> crewData = null)
        {
            VesselData newData = new VesselData();

            newData.craftURL = craftURL;
            newData.latitude = gpsCoords.x;
            newData.longitude = gpsCoords.y;
            newData.altitude = gpsCoords.z;

            newData.body = FlightGlobals.currentMainBody;
            newData.heading = heading;
            newData.pitch = pitch;
            newData.orbiting = false;
            newData.flagURL = HighLogic.CurrentGame.flagURL;
            newData.owned = true;
            newData.vesselType = VesselType.Ship;

            newData.crew = new List<CrewData>();

            return SpawnVessel(newData, out shipFacility, crewData);
        }

        private Vessel SpawnVessel(VesselData vesselData, out EditorFacility shipFacility, List<ProtoCrewMember> crewData = null)
        {
            shipFacility = EditorFacility.None;
            //Set additional info for landed vessels
            bool landed = false;
            if (!vesselData.orbiting)
            {
                landed = true;
                if (vesselData.altitude == null || vesselData.altitude < 0)
                {
                    vesselData.altitude = 35;
                }

                Vector3d pos = vesselData.body.GetRelSurfacePosition(vesselData.latitude, vesselData.longitude, vesselData.altitude.Value);

                vesselData.orbit = new Orbit(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, vesselData.body);
                vesselData.orbit.UpdateFromStateVectors(pos, vesselData.body.getRFrmVel(pos), vesselData.body, Planetarium.GetUniversalTime());
            }
            else
            {
                vesselData.orbit.referenceBody = vesselData.body;
            }

            ConfigNode[] partNodes;
            ShipConstruct shipConstruct = null;
            if (!string.IsNullOrEmpty(vesselData.craftURL))
            {
                var craftNode = ConfigNode.Load(vesselData.craftURL);
                shipConstruct = new ShipConstruct();
                if (!shipConstruct.LoadShip(craftNode))
                {
                    Debug.LogError("Ship file error!");
                    return null;
                }

                // Set the name
                if (string.IsNullOrEmpty(vesselData.name))
                {
                    vesselData.name = shipConstruct.shipName;
                }

                // Set some parameters that need to be at the part level
                uint missionID = (uint)Guid.NewGuid().GetHashCode();
                uint launchID = HighLogic.CurrentGame.launchID++;
                foreach (Part p in shipConstruct.parts)
                {
                    p.flightID = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                    p.missionID = missionID;
                    p.launchID = launchID;
                    p.flagURL = vesselData.flagURL ?? HighLogic.CurrentGame.flagURL;

                    // Had some issues with this being set to -1 for some ships - can't figure out
                    // why.  End result is the vessel exploding, so let's just set it to a positive
                    // value.
                    p.temperature = 1.0;
                }

                //add minimal crew
                //bool success = false;
                Part part = shipConstruct.parts.Find(p => p.protoModuleCrew.Count < p.CrewCapacity);

                // Add the crew member
                if (part != null)
                {
                    // Create the ProtoCrewMember
                    ProtoCrewMember crewMember = HighLogic.CurrentGame.CrewRoster.GetNewKerbal();
                    crewMember.gender = UnityEngine.Random.Range(0, 100) > 50
                        ? ProtoCrewMember.Gender.Female
                        : ProtoCrewMember.Gender.Male;
                    //crewMember.trait = "Pilot";

                    // Add them to the part
                    part.AddCrewmemberAt(crewMember, part.protoModuleCrew.Count);
                }

                // Create a dummy ProtoVessel, we will use this to dump the parts to a config node.
                // We can't use the config nodes from the .craft file, because they are in a
                // slightly different format than those required for a ProtoVessel (seriously
                // Squad?!?).
                ConfigNode empty = new ConfigNode();
                ProtoVessel dummyProto = new ProtoVessel(empty, null);
                Vessel dummyVessel = new Vessel();
                dummyVessel.parts = shipConstruct.Parts;
                dummyProto.vesselRef = dummyVessel;

                // Create the ProtoPartSnapshot objects and then initialize them
                foreach (Part p in shipConstruct.parts)
                {
                    dummyVessel.loaded = false;
                    p.vessel = dummyVessel;

                    dummyProto.protoPartSnapshots.Add(new ProtoPartSnapshot(p, dummyProto, true));
                }
                foreach (ProtoPartSnapshot p in dummyProto.protoPartSnapshots)
                {
                    p.storePartRefs();
                }

                // Create the ship's parts
                List<ConfigNode> partNodesL = new List<ConfigNode>();
                foreach (ProtoPartSnapshot snapShot in dummyProto.protoPartSnapshots)
                {
                    ConfigNode node = new ConfigNode("PART");
                    snapShot.Save(node);
                    partNodesL.Add(node);
                }
                partNodes = partNodesL.ToArray();
            }
            else
            {
                // Create crew member array
                ProtoCrewMember[] crewArray = new ProtoCrewMember[vesselData.crew.Count];
                int i = 0;
                foreach (CrewData cd in vesselData.crew)
                {
                    // Create the ProtoCrewMember
                    ProtoCrewMember crewMember = HighLogic.CurrentGame.CrewRoster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                    if (cd.name != null)
                    {
                        crewMember.KerbalRef.name = cd.name;
                    }

                    crewArray[i++] = crewMember;
                }

                // Create part nodes
                uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                partNodes = new ConfigNode[1];
                partNodes[0] = ProtoVessel.CreatePartNode(vesselData.craftPart.name, flightId, crewArray);

                // Default the size class
                //sizeClass = UntrackedObjectClass.A;

                // Set the name
                if (string.IsNullOrEmpty(vesselData.name))
                {
                    vesselData.name = vesselData.craftPart.name;
                }
            }

            // Create additional nodes
            ConfigNode[] additionalNodes = new ConfigNode[0];

            // Create the config node representation of the ProtoVessel
            ConfigNode protoVesselNode = ProtoVessel.CreateVesselNode(vesselData.name, vesselData.vesselType, vesselData.orbit, 0, partNodes, additionalNodes);

            // Additional seetings for a landed vessel
            if (!vesselData.orbiting)
            {
                Vector3d norm = vesselData.body.GetRelSurfaceNVector(vesselData.latitude, vesselData.longitude);

                bool splashed = false;// = landed && terrainHeight < 0.001;

                // Create the config node representation of the ProtoVessel
                // Note - flying is experimental, and so far doesn't work
                protoVesselNode.SetValue("sit", (splashed ? Vessel.Situations.SPLASHED : landed ?
                    Vessel.Situations.LANDED : Vessel.Situations.FLYING).ToString());
                protoVesselNode.SetValue("landed", (landed && !splashed).ToString());
                protoVesselNode.SetValue("splashed", splashed.ToString());
                protoVesselNode.SetValue("lat", vesselData.latitude.ToString());
                protoVesselNode.SetValue("lon", vesselData.longitude.ToString());
                protoVesselNode.SetValue("alt", vesselData.altitude.ToString());
                protoVesselNode.SetValue("landedAt", vesselData.body.name);

                // Figure out the additional height to subtract
                float lowest = float.MaxValue;
                if (shipConstruct != null)
                {
                    foreach (Part p in shipConstruct.parts)
                    {
                        foreach (Collider collider in p.GetComponentsInChildren<Collider>())
                        {
                            if (collider.gameObject.layer != 21 && collider.enabled)
                            {
                                lowest = Mathf.Min(lowest, collider.bounds.min.y);
                            }
                        }
                    }
                }
                else
                {
                    foreach (Collider collider in vesselData.craftPart.partPrefab.GetComponentsInChildren<Collider>())
                    {
                        if (collider.gameObject.layer != 21 && collider.enabled)
                        {
                            lowest = Mathf.Min(lowest, collider.bounds.min.y);
                        }
                    }
                }

                if (lowest == float.MaxValue)
                {
                    lowest = 0;
                }

                // Figure out the surface height and rotation
                Quaternion normal = Quaternion.LookRotation((Vector3)norm);// new Vector3((float)norm.x, (float)norm.y, (float)norm.z));
                Quaternion rotation = Quaternion.identity;
                float heading = vesselData.heading;
                if (shipConstruct == null)
                {
                    rotation = rotation * Quaternion.FromToRotation(Vector3.up, Vector3.back);
                }
                else if (shipConstruct.shipFacility == EditorFacility.SPH)
                {
                    rotation = rotation * Quaternion.FromToRotation(Vector3.forward, -Vector3.forward);
                    heading += 180.0f;
                }
                else
                {
                    rotation = rotation * Quaternion.FromToRotation(Vector3.up, Vector3.forward);
                    rotation = Quaternion.FromToRotation(Vector3.up, -Vector3.up) * rotation;

                    vesselData.heading = 0;
                    vesselData.pitch = 0;
                }

                rotation = rotation * Quaternion.AngleAxis(heading, Vector3.back);
                rotation = rotation * Quaternion.AngleAxis(vesselData.roll, Vector3.down);
                rotation = rotation * Quaternion.AngleAxis(vesselData.pitch, Vector3.left);

                // Set the height and rotation
                if (landed || splashed)
                {
                    float hgt = (shipConstruct != null ? shipConstruct.parts[0] : vesselData.craftPart.partPrefab).localRoot.attPos0.y - lowest;
                    hgt += vesselData.height + 35;
                    protoVesselNode.SetValue("hgt", hgt.ToString(), true);
                }
                protoVesselNode.SetValue("rot", KSPUtil.WriteQuaternion(normal * rotation), true);

                // Set the normal vector relative to the surface
                Vector3 nrm = (rotation * Vector3.forward);
                protoVesselNode.SetValue("nrm", nrm.x + "," + nrm.y + "," + nrm.z, true);
                protoVesselNode.SetValue("prst", false.ToString(), true);
            }

            // Add vessel to the game
            ProtoVessel protoVessel = HighLogic.CurrentGame.AddVessel(protoVesselNode);

            // Set the vessel size (FIXME various other vessel fields appear to not be set, e.g. CoM)
            protoVessel.vesselRef.vesselSize = shipConstruct.shipSize;
            shipFacility = shipConstruct.shipFacility;
            switch (shipFacility)
            {
                case EditorFacility.SPH:
                    protoVessel.vesselRef.vesselType = VesselType.Plane;
                    break;
                case EditorFacility.VAB:
                    protoVessel.vesselRef.vesselType = VesselType.Ship;
                    break;
                default:
                    break;
            }

            // Store the id for later use
            vesselData.id = protoVessel.vesselRef.id;
            // StartCoroutine(PlaceSpawnedVessel(protoVessel.vesselRef));

            //destroy prefabs
            foreach (Part p in FindObjectsOfType<Part>())
            {
                if (!p.vessel)
                {
                    Destroy(p.gameObject);
                }
            }

            return protoVessel.vesselRef;
        }

        private IEnumerator PlaceSpawnedVessel(Vessel v)
        {
            v.isPersistent = true;
            v.Landed = false;
            v.situation = Vessel.Situations.FLYING;
            while (v.packed)
            {
                yield return null;
            }
            v.SetWorldVelocity(Vector3d.zero);

            yield return null;
            FlightGlobals.ForceSetActiveVessel(v);
            yield return null;
            v.Landed = true;
            v.situation = Vessel.Situations.PRELAUNCH;
            v.GoOffRails();
            // v.IgnoreGForces(240);

            StageManager.BeginFlight();
        }

        internal class CrewData
        {
            public string name = null;
            public ProtoCrewMember.Gender? gender = null;
            public bool addToRoster = true;

            public CrewData() { }
            public CrewData(CrewData cd)
            {
                name = cd.name;
                gender = cd.gender;
                addToRoster = cd.addToRoster;
            }
        }
        private class VesselData
        {
            public string name = null;
            public Guid? id = null;
            public string craftURL = null;
            public AvailablePart craftPart = null;
            public string flagURL = null;
            public VesselType vesselType = VesselType.Ship;
            public CelestialBody body = null;
            public Orbit orbit = null;
            public double latitude = 0.0;
            public double longitude = 0.0;
            public double? altitude = null;
            public float height = 0.0f;
            public bool orbiting = false;
            public bool owned = false;
            public List<CrewData> crew = new List<CrewData>();
            public PQSCity pqsCity = null;
            public Vector3d pqsOffset;
            public float heading;
            public float pitch;
            public float roll;
        }
    }
}