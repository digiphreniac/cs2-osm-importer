using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace cs2_osm_importer
{
    internal static class OsmZoningParser
    {
        public static OsmZoningDocument Parse(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("OSM file not found for zoning parse", path);
n            var xml = new XmlDocument { XmlResolver = null };
            xml.Load(path);
n            var nodes = ReadNodes(xml);
            var doc = new OsmZoningDocument();

            // Parse ways (areas or closed ways commonly used for landuse/building footprints)            
            var wayNodes = xml.SelectNodes("/osm/way");
            if (wayNodes != null)
            {
                foreach (XmlElement wayEl in wayNodes)
                {
                    var tags = wayEl.SelectNodes("tag");
                    if (tags == null)
                        continue;
n                    var tagDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (XmlElement t in tags)
                        tagDict[t.GetAttribute("k")] = t.GetAttribute("v");

                    // pick up landuse, amenity, leisure
                    if (!tagDict.ContainsKey("landuse") && !tagDict.ContainsKey("amenity") && !tagDict.ContainsKey("leisure") && !tagDict.ContainsKey("building"))
                        continue;

                    // compute centroid from nd refs                    
                    var nds = wayEl.SelectNodes("nd");
                    if (nds == null)
                        continue;
n                    var points = new List<(double lat, double lon)>();
                    foreach (XmlElement nd in nds)
                    {
                        if (long.TryParse(nd.GetAttribute("ref"), out var id) && nodes.TryGetValue(id, out var n))
                            points.Add((n.Lat, n.Lon));
                    }
n                    if (points.Count == 0)
                        continue;
n                    var centroidLat = points.Average(p => p.lat);
                    var centroidLon = points.Average(p => p.lon);

                    var feature = new OsmZoningFeature { Id = ReadLong(wayEl, "id"), CentroidLat = centroidLat, CentroidLon = centroidLon };
                    foreach (var kv in tagDict)
                        feature.Tags[kv.Key] = kv.Value;
n                    if (tagDict.TryGetValue("landuse", out var landuse))
                    {
                        if (string.Equals(landuse, "residential", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.Residential;
                        else if (string.Equals(landuse, "retail", StringComparison.OrdinalIgnoreCase) || string.Equals(landuse, "commercial", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.Commercial;
                        else
                            feature.Type = ZoningType.Other;
                    }
                    else if (tagDict.TryGetValue("amenity", out var amenity))
                    {
                        if (string.Equals(amenity, "library", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.AmenityLibrary;
                        else if (string.Equals(amenity, "school", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.AmenitySchool;
                        else
                            feature.Type = ZoningType.Other;
                    }
                    else if (tagDict.TryGetValue("leisure", out var leisure))
                    {
                        if (string.Equals(leisure, "stadium", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.LeisureStadium;
                        else
                            feature.Type = ZoningType.Other;
                    }
                    else if (tagDict.TryGetValue("building", out var building))
                    {
                        // building footprint without explicit landuse - treat as residential by default
                        if (string.Equals(building, "apartments", StringComparison.OrdinalIgnoreCase) || string.Equals(building, "house", StringComparison.OrdinalIgnoreCase) || string.Equals(building, "residential", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.Residential;
                        else
                            feature.Type = ZoningType.Other;
                    }
n                    // Name + school inference
                    if (feature.Tags.TryGetValue("name", out var name))
                        feature.Name = name;
n                    if (feature.Type == ZoningType.AmenitySchool)
                    {
                        feature.InferredSchoolType = InferSchoolType(feature.Name);
                    }
n                    // Density heuristic
                    feature.Density = InferDensityFromTags(feature.Tags);
n                    doc.Features.Add(feature);
                }
            }
n            // Also parse standalone nodes that represent amenities (like a bench or small POI)            
            var nodeEls = xml.SelectNodes("/osm/node");
            if (nodeEls != null)
            {
                foreach (XmlElement nodeEl in nodeEls)
                {
                    var tags = nodeEl.SelectNodes("tag");
                    if (tags == null)
                        continue;
n                    var tagDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (XmlElement t in tags)
                        tagDict[t.GetAttribute("k")] = t.GetAttribute("v");
n                    if (!tagDict.ContainsKey("amenity") && !tagDict.ContainsKey("leisure"))
                        continue;
n                    var feature = new OsmZoningFeature { Id = ReadLong(nodeEl, "id"), CentroidLat = ReadDouble(nodeEl, "lat"), CentroidLon = ReadDouble(nodeEl, "lon") };
                    foreach (var kv in tagDict)
                        feature.Tags[kv.Key] = kv.Value;
n                    if (tagDict.TryGetValue("amenity", out var amenity))
                    {
                        if (string.Equals(amenity, "library", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.AmenityLibrary;
                        else if (string.Equals(amenity, "school", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.AmenitySchool;
                    }
                    else if (tagDict.TryGetValue("leisure", out var leisure))
                    {
                        if (string.Equals(leisure, "stadium", StringComparison.OrdinalIgnoreCase))
                            feature.Type = ZoningType.LeisureStadium;
                    }
n                    if (feature.Tags.TryGetValue("name", out var name))
                        feature.Name = name;

                    if (feature.Type == ZoningType.AmenitySchool)
                        feature.InferredSchoolType = InferSchoolType(feature.Name);
n                    feature.Density = InferDensityFromTags(feature.Tags);
                    doc.Features.Add(feature);
                }
            }
n            return doc;
        }
n        private static Dictionary<long, (double Lat, double Lon, Dictionary<string,string> Tags)> ReadNodes(XmlDocument xml)
        {
            var nodes = new Dictionary<long, (double, double, Dictionary<string,string>)>();
            var nodeElements = xml.SelectNodes("/osm/node");
            if (nodeElements == null)
                return nodes;
n            foreach (XmlElement nodeEl in nodeElements)
            {
                var id = ReadLong(nodeEl, "id");
                var lat = ReadDouble(nodeEl, "lat");
                var lon = ReadDouble(nodeEl, "lon");
                var tagDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var tags = nodeEl.SelectNodes("tag");
                if (tags != null)
                {
                    foreach (XmlElement t in tags)
                        tagDict[t.GetAttribute("k")] = t.GetAttribute("v");
                }
n                nodes[id] = (lat, lon, tagDict);
            }
n            return nodes;
        }
n        private static DensityLevel InferDensityFromTags(Dictionary<string,string> tags)
        {
            // Heuristics: prefer explicit building:levels or building type info
            if (tags == null)
                return DensityLevel.Low;
n            if (tags.TryGetValue("building:levels", out var levelsStr) && int.TryParse(levelsStr, out var levels))
            {
                if (levels >= 4) return DensityLevel.High;
                if (levels >= 2) return DensityLevel.Medium;
                return DensityLevel.Low;
            }
n            if (tags.TryGetValue("building", out var building))
            {
                if (string.Equals(building, "apartments", StringComparison.OrdinalIgnoreCase) || string.Equals(building, "terraced_house", StringComparison.OrdinalIgnoreCase))
                    return DensityLevel.High;
                if (string.Equals(building, "house", StringComparison.OrdinalIgnoreCase))
                    return DensityLevel.Low;
            }
n            // If landuse is retail/commercial, assume medium density by default
            if (tags.TryGetValue("landuse", out var landuse))
            {
                if (string.Equals(landuse, "retail", StringComparison.OrdinalIgnoreCase) || string.Equals(landuse, "commercial", StringComparison.OrdinalIgnoreCase))
                    return DensityLevel.Medium;
            }
n            return DensityLevel.Low;
        }
n        private static SchoolType InferSchoolType(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return SchoolType.Unknown;
n            var lower = name.ToLowerInvariant();
            if (lower.Contains("high") || lower.Contains("secondary") || lower.Contains("senior"))
                return SchoolType.HighSchool;
            return SchoolType.Elementary;
        }
n        private static double ReadDouble(XmlElement el, string attr)
        {
            var s = el.GetAttribute(attr);
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
            return 0.0;
        }
n        private static long ReadLong(XmlElement el, string attr)
        {
            var s = el.GetAttribute(attr);
            if (long.TryParse(s, out var v))
                return v;
            return 0L;
        }
    }
}