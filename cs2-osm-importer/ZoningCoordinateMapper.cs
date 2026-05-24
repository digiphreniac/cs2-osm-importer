using Unity.Mathematics;

namespace cs2_osm_importer
{
    // Coordinate transformation utilities for mapping OSM lat/lon to game world coordinates.
    internal static class ZoningCoordinateMapper
    {
        private const double EarthRadiusMeters = 6378137d;

        // Project a single lat/lon point to local world coordinates using Mercator-like projection.
        // anchorLat/anchorLon is the map center (used to compute relative offsets).
        public static float3 LatLonToWorldCoords(double lat, double lon, double anchorLat, double anchorLon)
        {
            // Convert lat/lon difference to meters using spherical approximations
            var latDiff = lat - anchorLat;
            var lonDiff = lon - anchorLon;

            // Approximate meter distances (at the anchor latitude)
            var latMeters = latDiff * (EarthRadiusMeters * System.Math.PI / 180d);
            var lonMeters = lonDiff * (EarthRadiusMeters * System.Math.PI / 180d) * System.Math.Cos(anchorLat * System.Math.PI / 180d);

            // Return as game world coordinates (Y=0 for ground level, X and Z for horizontal plane)
            return new float3((float)lonMeters, 0f, (float)latMeters);
        }

        // Reverse: game world coords to lat/lon
        public static (double lat, double lon) WorldCoordsToLatLon(float3 worldCoords, double anchorLat, double anchorLon)
        {
            var metersPerDegreeLat = EarthRadiusMeters * System.Math.PI / 180d;
            var metersPerDegreeLon = EarthRadiusMeters * System.Math.PI / 180d * System.Math.Cos(anchorLat * System.Math.PI / 180d);

            var latDelta = worldCoords.z / metersPerDegreeLat;
            var lonDelta = worldCoords.x / metersPerDegreeLon;

            return (anchorLat + latDelta, anchorLon + lonDelta);
        }
    }
}