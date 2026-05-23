using System.Collections.Generic;

namespace cs2_osm_importer
{
    public enum ZoningType { Residential, Commercial, AmenityLibrary, AmenitySchool, LeisureStadium, Other }
    public enum DensityLevel { Low, Medium, High }
    public enum SchoolType { Unknown, Elementary, HighSchool }

    internal sealed class OsmZoningFeature
    {
        public long Id { get; set; }
        public ZoningType Type { get; set; }
        public DensityLevel Density { get; set; } = DensityLevel.Low;
        public SchoolType InferredSchoolType { get; set; } = SchoolType.Unknown;
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        public double CentroidLat { get; set; }
        public double CentroidLon { get; set; }
    }

    internal sealed class OsmZoningDocument
    {
        public List<OsmZoningFeature> Features { get; } = new List<OsmZoningFeature>();
    }
}