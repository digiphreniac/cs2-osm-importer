using System;
using System.Reflection;
using Unity.Mathematics;

namespace cs2_osm_importer
{
    // Facade for applying zoning to the running game via reflection to avoid compile-time coupling.
    internal static class ZoningApplier
    {
        // Apply a zoning type and density to a world position in the game.
        // Uses reflection to call Game APIs without compile-time dependency.
        public static void ApplyZoneAtPosition(float3 worldPos, ZoningType type, DensityLevel density)
        {
            try
            {
                // Attempt to find and invoke a zoning API.
                // Pattern: Game.Tools.ZoningTool.ApplyZone(worldPos, zoneType, density) or similar.
                // This is a common pattern in Cities: Skylines 2; exact namespace/class may vary.

                // Try to get ZoningManager or similar
                var zoningManagerType = Type.GetType("Game.Zones.ZoningManager, Assembly-CSharp");
                if (zoningManagerType == null)
                    zoningManagerType = Type.GetType("Game.Tools.ZoningTool, Assembly-CSharp");
                if (zoningManagerType == null)
                    zoningManagerType = Type.GetType("ZoningManager, Assembly-CSharp");

                if (zoningManagerType == null)
                {
                    Mod.log.Warning($"OSM zoning: could not find zoning API type. Actual game zoning application skipped.");
                    return;
                }

                // Look for an ApplyZone or SetZone method
                MethodInfo applyMethod = zoningManagerType.GetMethod("ApplyZone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? zoningManagerType.GetMethod("SetZone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? zoningManagerType.GetMethod("CreateZone", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (applyMethod == null)
                {
                    Mod.log.Warning($"OSM zoning: could not find zoning apply method. Actual game zoning application skipped.");
                    return;
                }

                // Map our ZoningType enum to game enum (if needed)
                var gameZoneType = MapZoningTypeToGameZone(type);

                // Prepare density as an enum value or parameter
                var gameDensity = MapDensityToGameDensity(density);

                // Call the method with worldPos, zoneType, and density
                // Parameters depend on the actual method signature; common pattern:
                // ApplyZone(float3 position, ZoneType zoneType, DensityLevel density)
                var parameters = new object[] { worldPos, gameZoneType, gameDensity };
                applyMethod.Invoke(null, parameters);

                Mod.log.Info($"OSM zoning: applied {type} ({density}) at world pos {worldPos}");
            }
            catch (Exception ex)
            {
                Mod.log.Warning($"OSM zoning: failed to apply zone via reflection: {ex.Message}");
            }
        }

        private static object MapZoningTypeToGameZone(ZoningType type)
        {
            // Attempt to find and map to game's zone enum.
            // Common zones in CS2: Residential, Commercial, Industrial, Office, etc.
            var zoneEnum = Type.GetType("Game.Zones.ZoneType, Assembly-CSharp");
            if (zoneEnum == null)
                zoneEnum = Type.GetType("ZoneType, Assembly-CSharp");

            if (zoneEnum == null)
            {
                Mod.log.Warning("OSM zoning: could not find ZoneType enum");
                return 0; // default
            }

            return type switch
            {
                ZoningType.Residential => Enum.Parse(zoneEnum, "Residential", ignoreCase: true),
                ZoningType.Commercial => Enum.Parse(zoneEnum, "Commercial", ignoreCase: true),
                ZoningType.AmenityLibrary => Enum.Parse(zoneEnum, "Office", ignoreCase: true), // Library as Office zone
                ZoningType.AmenitySchool => Enum.Parse(zoneEnum, "Office", ignoreCase: true), // School as Office zone
                ZoningType.LeisureStadium => Enum.Parse(zoneEnum, "Commercial", ignoreCase: true), // Stadium as Commercial
                _ => Enum.Parse(zoneEnum, "Residential", ignoreCase: true)
            };
        }

        private static object MapDensityToGameDensity(DensityLevel density)
        {
            // Attempt to find game's density/district modifier enum.
            var densityEnum = Type.GetType("Game.Zones.DensityLevel, Assembly-CSharp");
            if (densityEnum == null)
                densityEnum = Type.GetType("DensityLevel, Assembly-CSharp");

            if (densityEnum == null)
            {
                // If no explicit enum, may use an integer or name convention (e.g., zone prefab names with Low/Med/High)
                return (int)density; // fallback to 0=Low, 1=Med, 2=High
            }

            return density switch
            {
                DensityLevel.Low => Enum.Parse(densityEnum, "Low", ignoreCase: true),
                DensityLevel.Medium => Enum.Parse(densityEnum, "Medium", ignoreCase: true),
                DensityLevel.High => Enum.Parse(densityEnum, "High", ignoreCase: true),
                _ => Enum.Parse(densityEnum, "Low", ignoreCase: true)
            };
        }
    }
}