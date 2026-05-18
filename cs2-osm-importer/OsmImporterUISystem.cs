using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Colossal.UI.Binding;
using Game.UI.Editor;
using Game.UI;

namespace cs2_osm_importer
{
    public partial class OsmImporterUISystem : UISystemBase
    {
        private ValueBinding<string> _status;
        private ValueBinding<bool> _importing;
        private ValueBinding<string> _defaultOsmPath;
        private ValueBinding<bool> _hasLoadedData;
        private ValueBinding<string> _roadTypeCounts;
        private OsmImportSystem _importSystem;
        private EditorToolUISystem _editorToolUISystem;
        private string _lastStatus = OsmLocalization.T("OsmImporter.Status.Ready", "Ready.");
        private bool _lastImporting;
        private bool _lastHasLoadedData;
        private string _lastRoadTypeCounts = "{}";

        protected override void OnCreate()
        {
            base.OnCreate();

            _importSystem = World.GetOrCreateSystemManaged<OsmImportSystem>();
            _editorToolUISystem = World.GetOrCreateSystemManaged<EditorToolUISystem>();

            _status = new ValueBinding<string>(Mod.Id, "importStatus", OsmLocalization.T("OsmImporter.Status.Ready", "Ready."), ValueWriters.Create<string>());
            _importing = new ValueBinding<bool>(Mod.Id, "isImporting", false, ValueWriters.Create<bool>());
            _defaultOsmPath = new ValueBinding<string>(Mod.Id, "defaultOsmPath", GetDefaultOsmPath(), ValueWriters.Create<string>());
            _hasLoadedData = new ValueBinding<bool>(Mod.Id, "hasLoadedData", false, ValueWriters.Create<bool>());
            _roadTypeCounts = new ValueBinding<string>(Mod.Id, "roadTypeCounts", "{}", ValueWriters.Create<string>());

            AddBinding(_status);
            AddBinding(_importing);
            AddBinding(_defaultOsmPath);
            AddBinding(_hasLoadedData);
            AddBinding(_roadTypeCounts);
            AddBinding(new TriggerBinding<string>(Mod.Id, "loadOSMData", LoadOSMData, ValueReaders.Create<string>()));
            AddBinding(new TriggerBinding<string>(Mod.Id, "generateOSMRoads", GenerateOSMRoads, ValueReaders.Create<string>()));
            AddBinding(new TriggerBinding<string>(Mod.Id, "deleteAllSegments", DeleteAllSegments, ValueReaders.Create<string>()));
#if DEBUG
            AddBinding(new ValueBinding<bool>(Mod.Id, "isDebugMode", true));
            AddBinding(new TriggerBinding<string>(Mod.Id, "tryValidate", TryValidateRoadNames, ValueReaders.Create<string>()));
#endif
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, Game.GameMode mode)
        {
            // UISystemBase's preload path can touch an uninitialized system state during editor startup.
            // We only need the bindings after the UI world is live.
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            RegisterEditorTool();
        }

        private void RegisterEditorTool()
        {
            var existingTools = _editorToolUISystem.tools;
            if (existingTools != null && existingTools.Any(tool => tool != null && tool.id == Mod.ToolId))
                return;

            var newTools = new IEditorTool[(existingTools?.Length ?? 0) + 1];
            var i = 0;
            if (existingTools != null)
            {
                foreach (var tool in existingTools)
                    newTools[i++] = tool;
            }

            newTools[i] = new OsmEditorTool(Unity.Entities.World.DefaultGameObjectInjectionWorld);
            _editorToolUISystem.tools = newTools;
        }

        private void LoadOSMData(string payload)
        {
            var options = ParseImportOptions(payload);
            foreach (var highwayType in ReadStringArray(payload, "importRoadTypes"))
                options.ImportHighwayTypes.Add(highwayType);

            if (!_importSystem.LoadOsmData(new OsmImportOptions
            {
                Path = options.Path,
                SimplifyToleranceMeters = options.SimplifyToleranceMeters,
                CurveToleranceMeters = options.CurveToleranceMeters,
                ScaleFactor = options.ScaleFactor <= 0f ? 1f : options.ScaleFactor,
                ElevationMeters = 0.2f,
                ImportHighwayTypes = options.ImportHighwayTypes,
                MaxWays = options.MaxWays <= 0 ? 10000 : options.MaxWays
            }))
            {
                _status.Update(OsmLocalization.T("OsmImporter.Status.LoadInProgress", "An OSM import is already running."));
                return;
            }

            UpdateImportBindings(force: true);
        }

        private void GenerateOSMRoads(string payload)
        {
            var options = new OsmGenerateOptions();
            foreach (var highwayType in ReadStringArray(payload, "roadTypes"))
                options.HighwayTypes.Add(highwayType);

            if (!_importSystem.GenerateRoads(options))
            {
                _status.Update(OsmLocalization.T("OsmImporter.Status.OperationInProgress", "An OSM operation is already running."));
                return;
            }

            UpdateImportBindings(force: true);
        }

        private void DeleteAllSegments(string payload)
        {
            if (!_importSystem.DeleteAllSegments())
            {
                _status.Update(OsmLocalization.T("OsmImporter.Status.OperationInProgress", "An OSM operation is already running."));
                return;
            }

            UpdateImportBindings(force: true);
        }

#if DEBUG
        private void TryValidateRoadNames(string payload)
        {
            _status.Update(_importSystem.TryValidateNames());
        }

#endif

        private static OsmImportOptions ParseImportOptions(string payload)
        {
            var hasImportRoadTypes = Regex.IsMatch(payload ?? string.Empty, "\"importRoadTypes\"\\s*:");
            var importRoadTypes = new HashSet<string>(ReadStringArray(payload, "importRoadTypes"), StringComparer.OrdinalIgnoreCase);

            return new OsmImportOptions
            {
                Path = ReadString(payload, "path"),
                SimplifyToleranceMeters = ReadFloat(payload, "tolerance", 1f),
                CurveToleranceMeters = ReadFloat(payload, "curveTolerance", 0.5f),
                ScaleFactor = ReadFloat(payload, "scaleFactor", 1f),
                UseImportHighwayTypesFilter = hasImportRoadTypes,
                LayerElevationStepMeters = ReadFloat(payload, "layerElevationStep", 10f),
                BridgeElevationMeters = ReadFloat(payload, "bridgeElevation", 10f),
                TunnelDepthMeters = ReadFloat(payload, "tunnelDepth", 10f),
                CoveredDepthMeters = ReadFloat(payload, "coveredDepth", 10f),
                EmbankmentElevationMeters = ReadFloat(payload, "embankmentElevation", 3f),
                CuttingDepthMeters = ReadFloat(payload, "cuttingDepth", 3f),
                ImportHighwayTypes = importRoadTypes,
                MaxWays = ReadInt(payload, "maxWays", 10000)
            };
        }

        private static string ReadString(string payload, string name)
        {
            var match = Regex.Match(payload ?? string.Empty, $"\"{name}\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
            return match.Success ? Regex.Unescape(match.Groups[1].Value) : string.Empty;
        }

        private static float ReadFloat(string payload, string name, float fallback)
        {
            var match = Regex.Match(payload ?? string.Empty, $"\"{name}\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            return match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        private static int ReadInt(string payload, string name, int fallback)
        {
            var match = Regex.Match(payload ?? string.Empty, $"\"{name}\"\\s*:\\s*(-?\\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        private static string[] ReadStringArray(string payload, string name)
        {
            var match = Regex.Match(payload ?? string.Empty, $"\"{name}\"\\s*:\\s*\\[(.*?)\\]");
            if (!match.Success)
                return Array.Empty<string>();

            return Regex.Matches(match.Groups[1].Value, "\"((?:\\\\.|[^\"])*)\"")
                .Cast<Match>()
                .Select(item => Regex.Unescape(item.Groups[1].Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            UpdateImportBindings(force: false);

            if (_importSystem.PendingNames.Count > 0)
            {
                _importSystem.ApplyPendingNames();
            }
        }

        private void UpdateImportBindings(bool force)
        {
            if (force || _lastStatus != _importSystem.Status)
            {
                _lastStatus = _importSystem.Status;
                _status.Update(_lastStatus);
            }

            if (force || _lastImporting != _importSystem.IsImporting)
            {
                _lastImporting = _importSystem.IsImporting;
                _importing.Update(_lastImporting);
            }

            if (force || _lastHasLoadedData != _importSystem.HasLoadedData)
            {
                _lastHasLoadedData = _importSystem.HasLoadedData;
                _hasLoadedData.Update(_lastHasLoadedData);
            }

            if (force || _lastRoadTypeCounts != _importSystem.RoadTypeCountsJson)
            {
                _lastRoadTypeCounts = _importSystem.RoadTypeCountsJson;
                _roadTypeCounts.Update(_lastRoadTypeCounts);
            }
        }

        private static string GetDefaultOsmPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
                return string.Empty;

            return Path.Combine(userProfile, "Downloads") + Path.DirectorySeparatorChar;
        }
    }
}
