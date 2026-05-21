using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.UI.Editor;
using Game.UI.Editor.Widgets;
using Game.Reflection;
using Game.UI.Widgets;

namespace cs2_osm_importer
{
    public partial class OsmImportPanelSystem : EditorPanelSystemBase
    {
        private static readonly string[] SupportedRoadTypes =
        {
            "motorway",
            "trunk",
            "primary",
            "secondary",
            "tertiary",
            "unclassified",
            "residential",
            "service",
            "living_street",
            "road",
            "motorway_link",
            "trunk_link",
            "primary_link",
            "secondary_link",
            "tertiary_link",
            "footway",
            "bridleway",
            "monorail",
            "rail",
            "light_rail",
            "subway",
            "tram",
            "track",
            "cycleway",
            "pedestrian",
        };

        internal static readonly Dictionary<string, RoadTypeMetadata> RoadTypeMetadataByType =
            new Dictionary<string, RoadTypeMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                { "motorway", new RoadTypeMetadata("OsmImporter.RoadType.Motorway.Name", "Motorway", "OsmImporter.RoadType.Motorway.Description", "High-capacity, access-controlled road for long-distance traffic.", "Media/Game/Icons/Highways.svg") },
                { "trunk", new RoadTypeMetadata("OsmImporter.RoadType.Trunk.Name", "Trunk", "OsmImporter.RoadType.Trunk.Description", "Major intercity road that carries important through traffic.", "Media/Game/Icons/HighwayTwoway4lanes.svg") },
                { "primary", new RoadTypeMetadata("OsmImporter.RoadType.Primary.Name", "Primary", "OsmImporter.RoadType.Primary.Description", "Main road connecting important towns, districts, and hubs.", "Media/Game/Icons/MediumRoadDivided.svg") },
                { "secondary", new RoadTypeMetadata("OsmImporter.RoadType.Secondary.Name", "Secondary", "OsmImporter.RoadType.Secondary.Description", "Significant connector between smaller towns and city districts.", "Media/Game/Icons/MediumRoad.svg") },
                { "tertiary", new RoadTypeMetadata("OsmImporter.RoadType.Tertiary.Name", "Tertiary", "OsmImporter.RoadType.Tertiary.Description", "Local connector that feeds traffic into higher-order roads.", "Media/Game/Icons/SmallRoads.svg") },
                { "unclassified", new RoadTypeMetadata("OsmImporter.RoadType.Unclassified.Name", "Unclassified", "OsmImporter.RoadType.Unclassified.Description", "Minor public road used for general local access.", "Media/Game/Icons/SmallRoad.svg") },
                { "residential", new RoadTypeMetadata("OsmImporter.RoadType.Residential.Name", "Residential", "OsmImporter.RoadType.Residential.Description", "Neighborhood street serving homes and local access.", "Media/Game/Icons/SmallRoadAsymmetric.svg") },
                { "service", new RoadTypeMetadata("OsmImporter.RoadType.Service.Name", "Service", "OsmImporter.RoadType.Service.Description", "Access road for buildings, parking, depots, and utilities.", "Media/Game/Icons/RoadsServices.svg") },
                { "living_street", new RoadTypeMetadata("OsmImporter.RoadType.LivingStreet.Name", "Living street", "OsmImporter.RoadType.LivingStreet.Description", "Low-speed shared street where pedestrians have priority.", "Media/Game/Icons/PedestrianStreet.svg") },
                { "road", new RoadTypeMetadata("OsmImporter.RoadType.Road.Name", "Road", "OsmImporter.RoadType.Road.Description", "Generic road tag used when no more specific type is known.", "Media/Game/Icons/Roads.svg") },
                { "motorway_link", new RoadTypeMetadata("OsmImporter.RoadType.MotorwayLink.Name", "Motorway link", "OsmImporter.RoadType.MotorwayLink.Description", "Ramp or connector linking a motorway to nearby roads.", "Media/Game/Icons/HighwayOneway2lanes.svg") },
                { "trunk_link", new RoadTypeMetadata("OsmImporter.RoadType.TrunkLink.Name", "Trunk link", "OsmImporter.RoadType.TrunkLink.Description", "Connector road feeding traffic into or out of a trunk route.", "Media/Game/Icons/HighwayOneway2lanes.svg") },
                { "primary_link", new RoadTypeMetadata("OsmImporter.RoadType.PrimaryLink.Name", "Primary link", "OsmImporter.RoadType.PrimaryLink.Description", "Connector between primary roads and the wider network.", "Media/Game/Icons/MediumRoadOneway.svg") },
                { "secondary_link", new RoadTypeMetadata("OsmImporter.RoadType.SecondaryLink.Name", "Secondary link", "OsmImporter.RoadType.SecondaryLink.Description", "Connector between secondary roads and local streets.", "Media/Game/Icons/SmallRoad.svg") },
                { "tertiary_link", new RoadTypeMetadata("OsmImporter.RoadType.TertiaryLink.Name", "Tertiary link", "OsmImporter.RoadType.TertiaryLink.Description", "Connector between tertiary roads and nearby local access.", "Media/Game/Icons/SmallRoadOneway1lane.svg") },
                { "footway", new RoadTypeMetadata("OsmImporter.RoadType.Footway.Name", "Footway", "OsmImporter.RoadType.Footway.Description", "Path designed for pedestrians, often separate from the road network.", "Media/Game/Icons/Pathways.svg") },
                { "bridleway", new RoadTypeMetadata("OsmImporter.RoadType.Bridleway.Name", "Bridleway", "OsmImporter.RoadType.Bridleway.Description", "Path intended for horse riding, also open to pedestrians.", "Media/Game/Icons/Pathways.svg") },
                { "monorail", new RoadTypeMetadata("OsmImporter.RoadType.Monorail.Name", "Monorail", "OsmImporter.RoadType.Monorail.Description", "Single-rail railway system for passenger transport.", "Media/Game/Icons/Train.svg") },
                { "rail", new RoadTypeMetadata("OsmImporter.RoadType.Rail.Name", "Rail", "OsmImporter.RoadType.Rail.Description", "Standard railway track for passenger and freight trains.", "Media/Game/Icons/TwoWayTrainTrack.svg") },
                { "light_rail", new RoadTypeMetadata("OsmImporter.RoadType.LightRail.Name", "Light rail", "OsmImporter.RoadType.LightRail.Description", "Urban rail system that combines tram-like flexibility with higher capacity.", "Media/Game/Icons/DoubleTramTrack.svg") },
                { "subway", new RoadTypeMetadata("OsmImporter.RoadType.Subway.Name", "Subway", "OsmImporter.RoadType.Subway.Description", "Underground metro system for high-capacity urban transit.", "Media/Game/Icons/DoubleTrainTrack.svg") },
                { "tram", new RoadTypeMetadata("OsmImporter.RoadType.Tram.Name", "Tram", "OsmImporter.RoadType.Tram.Description", "Street-running light rail system for city transport.", "Media/Game/Icons/DoubleTramTrack.svg") },
                { "track", new RoadTypeMetadata("OsmImporter.RoadType.Track.Name", "Track", "OsmImporter.RoadType.Track.Description", "Unpaved or rough rural road used for agriculture and forestry.", "Media/Game/Icons/eGravelRoad.svg") },
                { "cycleway", new RoadTypeMetadata("OsmImporter.RoadType.Cycleway.Name", "Cycleway", "OsmImporter.RoadType.Cycleway.Description", "Dedicated path or lane for bicycle traffic.", "Media/Game/Icons/TwoWayBikePath.svg") },
                { "pedestrian", new RoadTypeMetadata("OsmImporter.RoadType.Pedestrian.Name", "Pedestrian", "OsmImporter.RoadType.Pedestrian.Description", "Street or zone reserved for pedestrian use.", "Media/Game/Icons/PedestrianStreet.svg") },
            };

        private OsmImportSystem _importSystem;
        private Label _statusLabel;
        private Button _loadButton;
        private Button _resetDefaultsButton;
        private Button _generateButton;
        private Button _deleteButton;
        private Game.UI.Widgets.Group _loadConfirmationGroup;
        private Button _confirmLoadButton;
        private Button _cancelLoadButton;
        private PopupValueField<string> _pathField;
        private EditorSection _importDataSection;
        private IWidget _importRoadTypeSelectionWidget;
        private RoadTypeFilterAdapter _importRoadTypeFilterAdapter;
        private Label _importRoadTypesInstructionLabel;
        private IWidget _generateRoadTypeSelectionWidget;
        private RoadTypeFilterAdapter _generateRoadTypeFilterAdapter;
        private Label _generateRoadTypesInstructionLabel;
        private FloatSliderField _simplifyToleranceField;
        private FloatSliderField _curveToleranceField;
        private FloatSliderField _scaleFactorField;
        private IntSliderField _maxWaysField;
        private EditorSection _advancedSettingsSection;
        private EditorSection _structuralElevationSection;
        private EditorSection _generateAdvancedSettingsSection;
        private EditorSection _roadUpgradesSection;
        private FloatSliderField _generateIntersectionThresholdField;
        private Button _generateResetDefaultsButton;
        private readonly Dictionary<string, bool> _importRoadTypeSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _generateRoadTypeSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<RoadTypeToggleButton> _roadTypeTiles = new List<RoadTypeToggleButton>();
        private readonly List<string> _roadTypesOrder = new List<string>(SupportedRoadTypes);
        private static readonly HashSet<string> DefaultSelectedRoadTypes =
            new HashSet<string>(SupportedRoadTypes, StringComparer.OrdinalIgnoreCase);
        private string _lastRoadTypeCountsJson = string.Empty;
        private bool _lastHasLoadedData;
        private bool _lastHasGeneratedAssets;
        private HashSet<string> _generatedRoadTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _pendingGeneratedRoadTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _pathValue;
        private double _simplifyToleranceValue = 1d;
        private double _curveToleranceValue = 0.5d;
        private double _scaleFactorValue = 1d;
        private int _maxWaysValue = 10000;
        private double _layerElevationStepValue = 10d;
        private double _bridgeElevationValue = 10d;
        private double _tunnelDepthValue = 10d;
        private double _coveredDepthValue = 5d;
        private double _embankmentElevationValue = 3d;
        private double _cuttingDepthValue = 3d;
        private double _intersectionThresholdValue = 8d;
        private bool _generateBikeLanesValue = true;
        private bool _generateRoadsideTreesValue = true;
        private bool _generateTurnRestrictionsValue = true;
        private bool _generateCrosswalksValue = true;
        private bool _generateStopSignsValue = true;
        private bool _generateTrafficLightsValue = true;
        private bool _generateRestrictedLanesValue = true;
        private string _statusText = OsmLocalization.T("OsmImporter.Status.Ready", "Ready.");
        private int _fieldVersion;
        private bool _wasPanelActive;
        private bool _showLoadConfirmation;
        private bool _isPathInvalid;
        private bool _advancedSettingsExpanded = true;
        private bool _generateAdvancedSettingsExpanded = true;

        protected override void OnCreate()
        {
            base.OnCreate();
            _importSystem = World.GetOrCreateSystemManaged<OsmImportSystem>();

            title = OsmLocalization.T("OsmImporter.Panel.Title", "OSM Importer Tool");
            uiTag = "UITagPrefab:OSMImportPanel";

            SetPathValue(GetDefaultOsmPath());
            SetSimplifyToleranceValue(1d);
            SetCurveToleranceValue(0.5d);
            SetScaleFactorValue(1d);
            SetMaxWaysValue(10000);
            SelectDefaultRoadTypes();
            children = BuildChildren();
            UpdateStatus(_importSystem.Status);
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (ReferenceEquals(activeSubPanel, this))
            {
                if (!_wasPanelActive)
                    ResetAdvancedSettingsSection();
                _wasPanelActive = true;
            }
            else
            {
                _wasPanelActive = false;
            }

            if (_importSystem != null && _statusLabel != null)
            {
                UpdateImportSectionState();
                UpdateRoadTypeCounts();
                UpdateStatus(_importSystem.Status);
                FlushStatusRebuild();
                UpdateControlStates();
                UpdateGeneratedState();
            }

        }

        private List<IWidget> BuildChildren()
        {
            var content = new List<IWidget>();

            content.Add(new Label
            {
                displayName = OsmLocalization.T("OsmImporter.Label.ImportHeader", "OSM Import Tool"),
            });

            content.Add(new Divider());

            _statusLabel = new Label
            {
                displayName = _statusText,
            };

            _pathField = new PopupValueField<string>
            {
                displayName = OsmLocalization.T("OsmImporter.Label.Path", "OSM file path"),
                accessor = new TypedValueAccessor<string>(() => _pathValue, SetPathValue),
                icon = "Media/Editor/Folder.svg",
            };
            _pathField.popup = new FilePickerPopup(new[] { ".osm" }, () => _pathField.expanded = false);

            _simplifyToleranceField = new FloatSliderField
            {
                displayName = OsmLocalization.T("OsmImporter.Label.SimplifyTolerance", "Simplify tolerance (m)"),
                tooltip = OsmLocalization.T("OsmImporter.Tooltip.SimplifyTolerance", "Simplifies road geometry before import."),
                min = 0d,
                max = 10d,
                step = 0.1,
                fractionDigits = 1,
                stepMultiplier = 10d,
                unit = "m",
                accessor = new TypedValueAccessor<double>(() => _simplifyToleranceValue, SetSimplifyToleranceValue),
                valueVersion = () => _fieldVersion,
            };

            _curveToleranceField = new FloatSliderField
            {
                displayName = OsmLocalization.T("OsmImporter.Label.CurveTolerance", "Curve tolerance (m)"),
                tooltip = OsmLocalization.T("OsmImporter.Tooltip.CurveTolerance", "Controls how tightly curves are followed."),
                min = 0d,
                max = 5d,
                step = 0.1,
                fractionDigits = 1,
                stepMultiplier = 10d,
                unit = "m",
                accessor = new TypedValueAccessor<double>(() => _curveToleranceValue, SetCurveToleranceValue),
                valueVersion = () => _fieldVersion,
            };

            _scaleFactorField = new FloatSliderField
            {
                displayName = OsmLocalization.T("OsmImporter.Label.ScaleFactor", "Scale factor"),
                tooltip = OsmLocalization.T("OsmImporter.Tooltip.ScaleFactor", "Scales the imported map size."),
                min = 0.1d,
                max = 10d,
                step = 0.01,
                fractionDigits = 2,
                stepMultiplier = 10d,
                unit = "x",
                accessor = new TypedValueAccessor<double>(() => _scaleFactorValue, SetScaleFactorValue),
                valueVersion = () => _fieldVersion,
            };

            _maxWaysField = new IntSliderField
            {
                displayName = OsmLocalization.T("OsmImporter.Label.MaxWays", "Max ways"),
                tooltip = OsmLocalization.T("OsmImporter.Tooltip.MaxWays", "Limits how many OSM ways are imported."),
                min = 1000,
                max = 100000,
                step = 1000,
                stepMultiplier = 10,
                separateThousands = true,
                accessor = new TypedValueAccessor<int>(() => _maxWaysValue, SetMaxWaysValue),
                valueVersion = () => _fieldVersion,
            };

            _structuralElevationSection = new EditorSection
            {
                displayName = OsmLocalization.T("OsmImporter.Label.StructuralElevation", "Structural elevation"),
                expanded = true,
                children = new List<IWidget>
                {
                    new FloatSliderField
                    {
                        displayName = OsmLocalization.T("OsmImporter.Label.LayerElevationStep", "Layer step (m)"),
                        tooltip = OsmLocalization.T("OsmImporter.Tooltip.LayerElevationStep", "Height added for each OSM layer level."),
                        min = 0d,
                        max = 20d,
                        step = 0.1,
                        fractionDigits = 1,
                        stepMultiplier = 10d,
                        unit = "m",
                        accessor = new TypedValueAccessor<double>(() => _layerElevationStepValue, SetLayerElevationStepValue),
                        valueVersion = () => _fieldVersion,
                    },
                    new FloatSliderField
                    {
                        displayName = OsmLocalization.T("OsmImporter.Label.BridgeElevation", "Bridge height (m)"),
                        tooltip = OsmLocalization.T("OsmImporter.Tooltip.BridgeElevation", "Extra height added to roads tagged as bridges."),
                        min = 0d,
                        max = 20d,
                        step = 0.1,
                        fractionDigits = 1,
                        stepMultiplier = 10d,
                        unit = "m",
                        accessor = new TypedValueAccessor<double>(() => _bridgeElevationValue, SetBridgeElevationValue),
                        valueVersion = () => _fieldVersion,
                    },
                    new FloatSliderField
                    {
                        displayName = OsmLocalization.T("OsmImporter.Label.TunnelDepth", "Tunnel depth (m)"),
                        tooltip = OsmLocalization.T("OsmImporter.Tooltip.TunnelDepth", "Depth subtracted from roads tagged as tunnels."),
                        min = 0d,
                        max = 20d,
                        step = 0.1,
                        fractionDigits = 1,
                        stepMultiplier = 10d,
                        unit = "m",
                        accessor = new TypedValueAccessor<double>(() => _tunnelDepthValue, SetTunnelDepthValue),
                        valueVersion = () => _fieldVersion,
                    },
                    new FloatSliderField
                    {
                        displayName = OsmLocalization.T("OsmImporter.Label.CoveredDepth", "Covered depth (m)"),
                        tooltip = OsmLocalization.T("OsmImporter.Tooltip.CoveredDepth", "Depth subtracted from roads tagged as covered."),
                        min = 0d,
                        max = 20d,
                        step = 0.1,
                        fractionDigits = 1,
                        stepMultiplier = 10d,
                        unit = "m",
                        accessor = new TypedValueAccessor<double>(() => _coveredDepthValue, SetCoveredDepthValue),
                        valueVersion = () => _fieldVersion,
                    },
                    new FloatSliderField
                    {
                        displayName = OsmLocalization.T("OsmImporter.Label.EmbankmentElevation", "Embankment height (m)"),
                        tooltip = OsmLocalization.T("OsmImporter.Tooltip.EmbankmentElevation", "Height added to roads tagged as embankments."),
                        min = 0d,
                        max = 20d,
                        step = 0.1,
                        fractionDigits = 1,
                        stepMultiplier = 10d,
                        unit = "m",
                        accessor = new TypedValueAccessor<double>(() => _embankmentElevationValue, SetEmbankmentElevationValue),
                        valueVersion = () => _fieldVersion,
                    },
                    new FloatSliderField
                    {
                        displayName = OsmLocalization.T("OsmImporter.Label.CuttingDepth", "Cutting depth (m)"),
                        tooltip = OsmLocalization.T("OsmImporter.Tooltip.CuttingDepth", "Depth subtracted from roads tagged as cuttings."),
                        min = 0d,
                        max = 20d,
                        step = 0.1,
                        fractionDigits = 1,
                        stepMultiplier = 10d,
                        unit = "m",
                        accessor = new TypedValueAccessor<double>(() => _cuttingDepthValue, SetCuttingDepthValue),
                        valueVersion = () => _fieldVersion,
                    },
                },
            };

            _importRoadTypeSelectionWidget = BuildImportRoadTypeSelectionWidget();
            _importRoadTypesInstructionLabel = new Label
            {
                displayName = OsmLocalization.T("OsmImporter.Label.ImportRoadTypesInstructions", "Choose which road types should be loaded from the OSM file."),
                level = Label.Level.SubTitle,
            };

            _generateRoadTypeSelectionWidget = BuildGenerateRoadTypeSelectionWidget();
            _generateRoadTypesInstructionLabel = new Label
            {
                displayName = OsmLocalization.T("OsmImporter.Label.GenerateRoadTypesInstructions", "Choose which of the imported road types should be generated."),
                level = Label.Level.SubTitle,
            };

            _loadButton = new Button
            {
                displayName = OsmLocalization.T("OsmImporter.Button.LoadOSMData", "Load OSM Data"),
                action = PromptLoadOsmDataConfirmation,
            };

            _confirmLoadButton = new Button
            {
                displayName = OsmLocalization.T("OsmImporter.Button.Continue", "Continue"),
                action = ConfirmLoadOsmData,
            };

            _cancelLoadButton = new Button
            {
                displayName = OsmLocalization.T("OsmImporter.Button.Cancel", "Cancel"),
                action = CancelLoadOsmDataConfirmation,
            };

            _loadConfirmationGroup = new Game.UI.Widgets.Group
            {
                displayName = OsmLocalization.T("OsmImporter.Label.ConfirmImport", "Confirm OSM import"),
                hidden = () => !_showLoadConfirmation,
                children = new List<IWidget>
                {
                    new Label
                    {
                        displayName = OsmLocalization.T("OsmImporter.Label.LoadWarning", "Loading OSM data may temporarily freeze the game while the file is parsed and road definitions are generated."),
                        level = Label.Level.SubTitle,
                    },
                    _confirmLoadButton,
                    _cancelLoadButton,
                },
            };

            _resetDefaultsButton = new Button
            {
                displayName = OsmLocalization.T("OsmImporter.Button.ResetDefaults", "Reset to defaults"),
                action = ResetDefaults,
            };

            _advancedSettingsSection = new EditorSection
            {
                displayName = OsmLocalization.T("OsmImporter.Label.AdvancedSettings", "Advanced settings"),
                expanded = _advancedSettingsExpanded,
                children = new List<IWidget>
                {
                    _simplifyToleranceField,
                    _curveToleranceField,
                    _scaleFactorField,
                    _maxWaysField,
                    _structuralElevationSection,
                    _resetDefaultsButton,
                },
            };

            _importDataSection = new EditorSection
            {
                displayName = OsmLocalization.T("OsmImporter.Label.ImportData", "Import data"),
                expanded = true,
                children = new List<IWidget>
                {
                    new Label
                    {
                        displayName = OsmLocalization.T("OsmImporter.Label.ImportInstructions", "Pick an OSM input file and configure how the data is parsed before loading it into the game."),
                    },
                    _pathField,
                    _importRoadTypesInstructionLabel,
                    _importRoadTypeSelectionWidget,
                    _advancedSettingsSection,
                    _loadButton,
                    _loadConfirmationGroup,
                },
            };

            content.Add(_importDataSection);

            _generateButton = new Button
            {
                displayName = OsmLocalization.T("OsmImporter.Button.GenerateRoads", "Generate Roads"),
                action = GenerateRoads,
            };

            _deleteButton = new Button
            {
                    displayName = OsmLocalization.T("OsmImporter.Button.DeleteAllSegments", "Delete All Segments"),
                action = DeleteAllSegments,
            };

            _generateIntersectionThresholdField = new FloatSliderField
            {
                displayName = OsmLocalization.T("IntersectionThreshold", "Intersection threshold (m)"),
                tooltip = OsmLocalization.T("IntersectionThreshold.Description", "Maximum XZ distance from a road for a pillar to be considered intersecting (1-20m)."),
                min = 1d,
                max = 20d,
                step = 0.5,
                fractionDigits = 1,
                stepMultiplier = 10d,
                unit = "m",
                accessor = new TypedValueAccessor<double>(() => _intersectionThresholdValue, SetIntersectionThresholdValue),
                valueVersion = () => _fieldVersion,
            };

            _generateResetDefaultsButton = new Button
            {
                displayName = OsmLocalization.T("OsmImporter.Button.ResetDefaults", "Reset to defaults"),
                action = ResetGenerateDefaults,
            };

            _roadUpgradesSection = new EditorSection
            {
                displayName = OsmLocalization.T("OsmImporter.Label.RoadUpgrades", "Road Upgrades"),
                expanded = true,
                disabled = () => _importSystem.IsImporting || !_importSystem.HasLoadedData,
                children = new List<IWidget>
                {
                    BuildGeneratePropertyToggle("OsmImporter.Label.GenerateBikeLanes", "Bike lanes", () => _generateBikeLanesValue, SetGenerateBikeLanesValue),
                    BuildGeneratePropertyToggle("OsmImporter.Label.GenerateRoadsideTrees", "Roadside trees", () => _generateRoadsideTreesValue, SetGenerateRoadsideTreesValue),
                    BuildGeneratePropertyToggle("OsmImporter.Label.GenerateTurnRestrictions", "Turn restrictions", () => _generateTurnRestrictionsValue, SetGenerateTurnRestrictionsValue),
                    BuildGeneratePropertyToggle("OsmImporter.Label.GenerateCrosswalks", "Crosswalks", () => _generateCrosswalksValue, SetGenerateCrosswalksValue),
                    BuildGeneratePropertyToggle("OsmImporter.Label.GenerateStopSigns", "Stop signs", () => _generateStopSignsValue, SetGenerateStopSignsValue),
                    BuildGeneratePropertyToggle("OsmImporter.Label.GenerateTrafficLights", "Traffic lights", () => _generateTrafficLightsValue, SetGenerateTrafficLightsValue),
                    BuildGeneratePropertyToggle("OsmImporter.Label.GenerateRestrictedLanes", "Restricted bus lanes", () => _generateRestrictedLanesValue, SetGenerateRestrictedLanesValue),
                },
            };

            _generateAdvancedSettingsSection = new EditorSection
            {
                displayName = OsmLocalization.T("OsmImporter.Label.AdvancedSettings", "Advanced settings"),
                expanded = _generateAdvancedSettingsExpanded,
                disabled = () => _importSystem.IsImporting || !_importSystem.HasLoadedData,
                children = new List<IWidget>
                {
                    _generateIntersectionThresholdField,
                    _generateResetDefaultsButton,
                },
            };

            var generateChildren = new List<IWidget>
            {
                _generateRoadTypesInstructionLabel,
                _generateRoadTypeSelectionWidget,
                _roadUpgradesSection,
                _generateAdvancedSettingsSection,
                _generateButton,
                _deleteButton,
            };

            var shouldShowDetail = _importSystem != null && _importSystem.IsImporting && _statusText.Contains(" | ");
            if (shouldShowDetail)
            {
                var parts = _statusText.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i += 3)
                {
                    var rowChildren = new List<IWidget>();
                    for (var j = i; j < i + 3 && j < parts.Length; j++)
                        rowChildren.Add(new Label { displayName = parts[j].Trim() });
                    generateChildren.Add(CreateRow(rowChildren));
                }
            }

            var generateSection = new EditorSection
            {
                displayName = OsmLocalization.T("OsmImporter.Label.GenerateRoads", "Generate Roads"),
                expanded = true,
                disabled = () => _importSystem.IsImporting || !_importSystem.HasLoadedData,
                children = generateChildren,
            };

            content.Add(generateSection);

            var scrollable = new Scrollable
            {
                direction = Direction.Vertical,
                children = content,
            };
            SetWidgetBoolMember(scrollable, "padding", false);

            var rootChildren = new List<IWidget> { scrollable };

            var summaryText = _importSystem?.StatusSummary ?? string.Empty;
            var bottomText = !string.IsNullOrEmpty(summaryText) ? summaryText : (shouldShowDetail ? string.Empty : _statusText);
            if (!string.IsNullOrEmpty(bottomText))
            {
                rootChildren.Add(new Divider());
                rootChildren.Add(BuildStatusFooter(bottomText));
            }

            return rootChildren;
        }

        private void LoadOsmData()
        {
            var options = new OsmImportOptions
            {
                Path = _pathValue ?? string.Empty,
                SimplifyToleranceMeters = (float)_simplifyToleranceValue,
                CurveToleranceMeters = (float)_curveToleranceValue,
                ScaleFactor = (float)_scaleFactorValue,
                ElevationMeters = 0.2f,
                UseImportHighwayTypesFilter = true,
                ImportHighwayTypes = new HashSet<string>(GetSelectedRoadTypes(_importRoadTypeSelections), StringComparer.OrdinalIgnoreCase),
                LayerElevationStepMeters = (float)_layerElevationStepValue,
                BridgeElevationMeters = (float)_bridgeElevationValue,
                TunnelDepthMeters = (float)_tunnelDepthValue,
                CoveredDepthMeters = (float)_coveredDepthValue,
                EmbankmentElevationMeters = (float)_embankmentElevationValue,
                CuttingDepthMeters = (float)_cuttingDepthValue,
                MaxWays = _maxWaysValue,
            };

            if (_isPathInvalid)
            {
                UpdateStatus(OsmLocalization.T("OsmImporter.Status.InvalidPath", "The selected file path is not a valid OSM file."));
                return;
            }

            if (!_importSystem.LoadOsmData(options))
            {
                UpdateStatus(OsmLocalization.T("OsmImporter.Status.LoadInProgress", "An OSM import is already running."));
                return;
            }

            UpdateStatus(_importSystem.Status);
            FlushStatusRebuild();
        }

        private void PromptLoadOsmDataConfirmation()
        {
            _showLoadConfirmation = true;
        }

        private void ConfirmLoadOsmData()
        {
            _showLoadConfirmation = false;
            LoadOsmData();
        }

        private void CancelLoadOsmDataConfirmation()
        {
            _showLoadConfirmation = false;
        }

        private void GenerateRoads()
        {
            var selectedTypes = GetSelectedGenerateRoadTypes().ToList();
            var options = new OsmGenerateOptions
            {
                IntersectionThresholdMeters = (float)_intersectionThresholdValue,
                GenerateBikeLanes = _generateBikeLanesValue,
                GenerateRoadsideTrees = _generateRoadsideTreesValue,
                GenerateTurnRestrictions = _generateTurnRestrictionsValue,
                GenerateCrosswalks = _generateCrosswalksValue,
                GenerateStopSigns = _generateStopSignsValue,
                GenerateTrafficLights = _generateTrafficLightsValue,
                GenerateRestrictedLanes = _generateRestrictedLanesValue,
            };
            foreach (var roadType in selectedTypes)
            {
                options.HighwayTypes.Add(roadType);
            }

            if (!_importSystem.GenerateRoads(options))
            {
                UpdateStatus(OsmLocalization.T("OsmImporter.Status.OperationInProgress", "An OSM operation is already running."));
                return;
            }

            _pendingGeneratedRoadTypes.Clear();
            foreach (var roadType in selectedTypes)
                _pendingGeneratedRoadTypes.Add(roadType);

            UpdateStatus(_importSystem.Status);
        }

        private void DeleteAllSegments()
        {
            if (!_importSystem.DeleteAllSegments())
            {
                UpdateStatus(OsmLocalization.T("OsmImporter.Status.OperationInProgress", "An OSM operation is already running."));
                return;
            }

            UpdateStatus(_importSystem.Status);
        }

        private void ResetDefaults()
        {
            SetSimplifyToleranceValue(1d);
            SetCurveToleranceValue(0.5d);
            SetScaleFactorValue(1d);
            SetMaxWaysValue(10000);
            SetLayerElevationStepValue(10d);
            SetBridgeElevationValue(10d);
            SetTunnelDepthValue(10d);
            SetCoveredDepthValue(10d);
            SetEmbankmentElevationValue(3d);
            SetCuttingDepthValue(3d);
            ResetAdvancedSettingsSection();
            UpdateStatus(OsmLocalization.T("OsmImporter.Status.DefaultsRestored", "Defaults restored."));
        }

        private void ResetGenerateDefaults()
        {
            SetIntersectionThresholdValue(8d);
            SetGenerateBikeLanesValue(true);
            SetGenerateRoadsideTreesValue(true);
            SetGenerateTurnRestrictionsValue(true);
            SetGenerateCrosswalksValue(true);
            SetGenerateStopSignsValue(true);
            SetGenerateTrafficLightsValue(true);
            SetGenerateRestrictedLanesValue(true);
            BumpFieldVersion();
            children = BuildChildren();
            UpdateStatus(OsmLocalization.T("OsmImporter.Status.DefaultsRestored", "Defaults restored."));
        }

        private void SelectDefaultRoadTypes()
        {
            SelectDefaultRoadTypes(_importRoadTypeSelections);
        }

        private void SelectDefaultRoadTypes(Dictionary<string, bool> selections)
        {
            foreach (var roadType in SupportedRoadTypes)
                selections[roadType] = DefaultSelectedRoadTypes.Contains(roadType);

            BumpFieldVersion();
            SyncRoadTypeFilterAdapters();
        }

        private bool IsRoadTypeImported(string roadType)
        {
            return _importSystem?.RoadTypeCountsByType != null
                && _importSystem.RoadTypeCountsByType.TryGetValue(roadType ?? string.Empty, out var count)
                && count > 0;
        }

        private void ResetAdvancedSettingsSection()
        {
            if (_advancedSettingsSection != null)
                _advancedSettingsSection.expanded = true;
        }

        private void SetRoadTypeSelection(Dictionary<string, bool> selections, string roadType, bool selected)
        {
            if (string.IsNullOrWhiteSpace(roadType) || selections == null)
                return;

            selections[roadType] = selected;
            BumpFieldVersion();
            SyncRoadTypeFilterAdapters();
            RefreshSelectedRoadTypes();
        }

        private IEnumerable<string> GetSelectedImportRoadTypes()
        {
            return GetSelectedRoadTypes(_importRoadTypeSelections);
        }

        private IEnumerable<string> GetSelectedGenerateRoadTypes()
        {
            return GetSelectedRoadTypes(_generateRoadTypeSelections)
                .Where(IsRoadTypeImported);
        }

        private IEnumerable<string> GetSelectedRoadTypes(Dictionary<string, bool> selections)
        {
            return _roadTypesOrder.Where(roadType => selections.TryGetValue(roadType, out var selected) && selected);
        }

        private string GetSelectedRoadTypesSummary()
        {
            var selected = GetSelectedImportRoadTypes().ToList();
            if (selected.Count == 0)
                return OsmLocalization.T("OsmImporter.Value.None", "none");

            if (selected.Count == _roadTypesOrder.Count)
                return OsmLocalization.T("OsmImporter.Value.AllSupportedTypes", "all supported types");

            if (selected.Count <= 3)
                return string.Join(", ", selected.Select(GetRoadTypeDisplayName));

            return OsmLocalization.F("OsmImporter.Format.SelectedCount", "{0} selected", selected.Count);
        }

        private string _lastDisplayedStatus = string.Empty;
        private bool _statusNeedsRebuild;

        private void UpdateStatus(string status)
        {
            var text = string.IsNullOrWhiteSpace(status) ? OsmLocalization.T("OsmImporter.Status.Ready", "Ready.") : status;
            _statusText = text;
            if (_statusNeedsRebuild || _lastDisplayedStatus != text)
            {
                _lastDisplayedStatus = text;
                _statusNeedsRebuild = true;
            }
        }

        private void FlushStatusRebuild()
        {
            if (!_statusNeedsRebuild || _statusLabel == null)
                return;
            _statusNeedsRebuild = false;
            children = BuildChildren();
        }

        private void UpdateImportSectionState()
        {
            if (_importSystem == null)
                return;

            if (_importSystem.HasLoadedData && !_lastHasLoadedData)
            {
                _advancedSettingsExpanded = false;
                if (_advancedSettingsSection != null)
                    _advancedSettingsSection.expanded = false;
                children = BuildChildren();
            }

            _lastHasLoadedData = _importSystem.HasLoadedData;
        }

        private void UpdateControlStates()
        {
            var isBusy = _importSystem.IsImporting;
            var hasLoadedData = _importSystem.HasLoadedData;
            var hasGeneratedAssets = _importSystem.HasGeneratedAssets;
            var isGenerateSectionDisabled = isBusy || !hasLoadedData;

            SetWidgetObjectMember(_generateRoadTypeSelectionWidget, "disabled", new Func<bool>(() => isGenerateSectionDisabled));

            if (_generateAdvancedSettingsSection != null)
                _generateAdvancedSettingsSection.disabled = () => isGenerateSectionDisabled;

            if (_roadUpgradesSection != null)
                _roadUpgradesSection.disabled = () => isGenerateSectionDisabled;

            SetWidgetObjectMember(_generateIntersectionThresholdField, "disabled", new Func<bool>(() => isGenerateSectionDisabled));
            SetWidgetObjectMember(_generateResetDefaultsButton, "disabled", new Func<bool>(() => isGenerateSectionDisabled));

            if (_loadButton != null)
                _loadButton.disabled = () => isBusy || hasGeneratedAssets || _isPathInvalid || _importRoadTypeSelections.Values.All(v => !v);

            if (_resetDefaultsButton != null)
                _resetDefaultsButton.disabled = () => isBusy;

            if (_generateButton != null)
                _generateButton.disabled = () => isGenerateSectionDisabled || _generateRoadTypeSelections.Values.All(v => !v);

            if (_deleteButton != null)
                _deleteButton.disabled = () => isGenerateSectionDisabled || !hasGeneratedAssets;
        }

        private void UpdateGeneratedState()
        {
            if (_importSystem == null)
                return;

            var hasGeneratedAssets = _importSystem.HasGeneratedAssets;

            if (hasGeneratedAssets && !_lastHasGeneratedAssets)
            {
                foreach (var roadType in _pendingGeneratedRoadTypes)
                {
                    _generatedRoadTypes.Add(roadType);
                    _generateRoadTypeSelections[roadType] = false;
                }
                _pendingGeneratedRoadTypes.Clear();
                SyncRoadTypeFilterAdapters();
                RefreshSelectedRoadTypes();
            }
            else if (!hasGeneratedAssets && _lastHasGeneratedAssets)
            {
                _generatedRoadTypes.Clear();
                _pendingGeneratedRoadTypes.Clear();
                SyncRoadTypeFilterAdapters();
                RefreshSelectedRoadTypes();
            }

            _lastHasGeneratedAssets = hasGeneratedAssets;
        }

        private void SetPathValue(string value)
        {
            _pathValue = value ?? string.Empty;
            BumpFieldVersion();

            _isPathInvalid = !IsValidOsmPath(_pathValue);
            if (_isPathInvalid)
            {
                UpdateStatus(OsmLocalization.T("OsmImporter.Status.InvalidPath", "The selected file path is not a valid OSM file."));
            }
            else if (_importSystem != null)
            {
                UpdateStatus(_importSystem.Status);
                FlushStatusRebuild();
            }
            else
            {
                UpdateStatus(OsmLocalization.T("OsmImporter.Status.Ready", "Ready."));
            }
        }

        private static bool IsValidOsmPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!File.Exists(path))
                return false;

            return string.Equals(Path.GetExtension(path), ".osm", StringComparison.OrdinalIgnoreCase);
        }

        private void SetSimplifyToleranceValue(double value)
        {
            _simplifyToleranceValue = value;
            BumpFieldVersion();
        }

        private void SetCurveToleranceValue(double value)
        {
            _curveToleranceValue = value;
            BumpFieldVersion();
        }

        private void SetScaleFactorValue(double value)
        {
            _scaleFactorValue = value;
            BumpFieldVersion();
        }

        private void SetMaxWaysValue(int value)
        {
            _maxWaysValue = value;
            BumpFieldVersion();
        }

        private void SetLayerElevationStepValue(double value)
        {
            _layerElevationStepValue = value;
            BumpFieldVersion();
        }

        private void SetBridgeElevationValue(double value)
        {
            _bridgeElevationValue = value;
            BumpFieldVersion();
        }

        private void SetTunnelDepthValue(double value)
        {
            _tunnelDepthValue = value;
            BumpFieldVersion();
        }

        private void SetCoveredDepthValue(double value)
        {
            _coveredDepthValue = value;
            BumpFieldVersion();
        }

        private void SetEmbankmentElevationValue(double value)
        {
            _embankmentElevationValue = value;
            BumpFieldVersion();
        }

        private void SetCuttingDepthValue(double value)
        {
            _cuttingDepthValue = value;
            BumpFieldVersion();
        }

        private void SetIntersectionThresholdValue(double value)
        {
            _intersectionThresholdValue = value;
            BumpFieldVersion();
        }

        private void SetGenerateBikeLanesValue(bool value)
        {
            _generateBikeLanesValue = value;
            BumpFieldVersion();
        }

        private void SetGenerateRoadsideTreesValue(bool value)
        {
            _generateRoadsideTreesValue = value;
            BumpFieldVersion();
        }

        private void SetGenerateTurnRestrictionsValue(bool value)
        {
            _generateTurnRestrictionsValue = value;
            BumpFieldVersion();
        }

        private void SetGenerateCrosswalksValue(bool value)
        {
            _generateCrosswalksValue = value;
            BumpFieldVersion();
        }

        private void SetGenerateStopSignsValue(bool value)
        {
            _generateStopSignsValue = value;
            BumpFieldVersion();
        }

        private void SetGenerateTrafficLightsValue(bool value)
        {
            _generateTrafficLightsValue = value;
            BumpFieldVersion();
        }

        private void SetGenerateRestrictedLanesValue(bool value)
        {
            _generateRestrictedLanesValue = value;
            BumpFieldVersion();
        }

        private void RefreshSelectedRoadTypes()
        {
            BumpFieldVersion();
            children = BuildChildren();
        }

        private void UpdateRoadTypeCounts()
        {
            if (_importSystem == null)
                return;

            var countsJson = _importSystem.RoadTypeCountsJson ?? string.Empty;
            if (countsJson == _lastRoadTypeCountsJson)
                return;

            _lastRoadTypeCountsJson = countsJson;

            // Whenever the set of imported road types changes, refresh the generate dictionary
            // so it reflects exactly what was imported. Every newly-imported road type starts out
            // selected so the user can click Generate immediately; types that aren't in the new
            // import are removed entirely from the generate selection.
            if (_importSystem.HasLoadedData
                && _importSystem.RoadTypeCountsByType != null
                && _importSystem.RoadTypeCountsByType.Count > 0)
            {
                _generateRoadTypeSelections.Clear();
                foreach (var roadType in _roadTypesOrder)
                {
                    if (IsRoadTypeImported(roadType))
                        _generateRoadTypeSelections[roadType] = true;
                }
            }
            else
            {
                _generateRoadTypeSelections.Clear();
            }

            SyncRoadTypeFilterAdapters();
            RefreshSelectedRoadTypes();
        }

        private IWidget BuildImportRoadTypeSelectionWidget()
        {
            _importRoadTypeFilterAdapter ??= new RoadTypeFilterAdapter(this, _importRoadTypeSelections, false);
            _importRoadTypeFilterAdapter.SyncAvailableRoadTypes(GetAvailableImportRoadTypes());
            _importRoadTypeFilterAdapter.SyncActiveRoadTypes(GetSelectedImportRoadTypes());

            return new FilterMenu
            {
                adapter = _importRoadTypeFilterAdapter,
            };
        }

        private IWidget BuildGenerateRoadTypeSelectionWidget()
        {
            _generateRoadTypeFilterAdapter ??= new RoadTypeFilterAdapter(this, _generateRoadTypeSelections, true);
            _generateRoadTypeFilterAdapter.SyncAvailableRoadTypes(GetAvailableGenerateRoadTypes());
            _generateRoadTypeFilterAdapter.SyncActiveRoadTypes(GetSelectedGenerateRoadTypes());

            return new FilterMenu
            {
                adapter = _generateRoadTypeFilterAdapter,
            };
        }

        private IEnumerable<string> GetAvailableImportRoadTypes()
        {
            // The import filter always exposes every supported OSM road type because the user is
            // deciding what to extract from the OSM file before any data has been loaded.
            return _roadTypesOrder;
        }

        private IEnumerable<string> GetAvailableGenerateRoadTypes()
        {
            // The generate filter only exposes road types that the most recent import actually
            // produced. Before any import (or after a failed/empty import) the list stays empty.
            // Already-generated road types are excluded so they can't be toggled again.
            if (_importSystem?.HasLoadedData == true && _importSystem.RoadTypeCountsByType != null && _importSystem.RoadTypeCountsByType.Count > 0)
            {
                return _roadTypesOrder.Where(roadType =>
                    _importSystem.RoadTypeCountsByType.TryGetValue(roadType, out var count)
                    && count > 0
                    && !_generatedRoadTypes.Contains(roadType));
            }

            return Array.Empty<string>();
        }

        private void SyncRoadTypeFilterAdapters()
        {
            if (_importRoadTypeFilterAdapter != null)
            {
                _importRoadTypeFilterAdapter.SyncAvailableRoadTypes(GetAvailableImportRoadTypes());
                _importRoadTypeFilterAdapter.SyncActiveRoadTypes(GetSelectedImportRoadTypes());
            }

            if (_generateRoadTypeFilterAdapter != null)
            {
                _generateRoadTypeFilterAdapter.SyncAvailableRoadTypes(GetAvailableGenerateRoadTypes());
                _generateRoadTypeFilterAdapter.SyncActiveRoadTypes(GetSelectedGenerateRoadTypes());
            }
        }

        private bool TryPopulateRoadTypeFilterMenu(FilterMenu menu)
        {
            if (menu == null)
                return false;

            var methods = typeof(FilterMenu)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, "AddFilter", StringComparison.OrdinalIgnoreCase))
                .Where(method => method.GetParameters().Length > 0)
                .OrderBy(method => method.GetParameters().Length)
                .ToList();

            var addedAny = false;

            foreach (var roadType in _roadTypesOrder)
            {
                var selected = _importRoadTypeSelections.TryGetValue(roadType, out var value) && value;
                var metadata = TryGetRoadTypeMetadata(roadType, out var roadMetadata) ? roadMetadata : null;
                var label = metadata?.DisplayName ?? GetRoadTypeDisplayName(roadType);
                var tooltip = metadata?.Description ?? string.Empty;

                var added = false;
                foreach (var method in methods)
                {
                    if (TryInvokeAddFilter(menu, method, roadType, label, tooltip, selected))
                    {
                        added = true;
                        addedAny = true;
                        break;
                    }
                }

                if (!added)
                    break;
            }

            return addedAny;
        }

        private bool TryInvokeAddFilter(FilterMenu menu, MethodInfo method, string roadType, string label, string tooltip, bool selected)
        {
            if (menu == null || method == null)
                return false;

            var parameters = method.GetParameters();
            var args = new object[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;

                if (parameterType == typeof(string))
                {
                    args[i] = i == 0 ? label : tooltip;
                    continue;
                }

                if (parameterType == typeof(bool))
                {
                    args[i] = selected;
                    continue;
                }

                if (parameterType == typeof(int))
                {
                    args[i] = _roadTypesOrder.IndexOf(roadType);
                    continue;
                }

                if (parameterType == typeof(float))
                {
                    args[i] = 0f;
                    continue;
                }

                if (parameterType == typeof(double))
                {
                    args[i] = 0d;
                    continue;
                }

                if (typeof(Action<bool>).IsAssignableFrom(parameterType))
                {
                    args[i] = new Action<bool>(value => SetRoadTypeSelection(_importRoadTypeSelections, roadType, value));
                    continue;
                }

                if (typeof(Action).IsAssignableFrom(parameterType))
                {
                    args[i] = new Action(() => { });
                    continue;
                }

                if (parameterType.IsAssignableFrom(typeof(TypedValueAccessor<bool>)))
                {
                    args[i] = new TypedValueAccessor<bool>(() => _importRoadTypeSelections.TryGetValue(roadType, out var value) && value, value => SetRoadTypeSelection(_importRoadTypeSelections, roadType, value));
                    continue;
                }

                if (!parameterType.IsValueType)
                {
                    args[i] = null;
                    continue;
                }

                args[i] = Activator.CreateInstance(parameterType);
            }

            try
            {
                method.Invoke(menu, args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int GetRoadTypeCount(string roadType)
        {
            if (_importSystem?.RoadTypeCountsByType != null && _importSystem.RoadTypeCountsByType.TryGetValue(roadType ?? string.Empty, out var count))
                return count;

            return 0;
        }

        private static string FormatRoadTypeLabel(string displayName, int count)
        {
            return OsmLocalization.F("OsmImporter.Format.RoadTypeCount", "{0} ({1})", displayName, count);
        }

        private static string GetRoadTypeDisplayName(string roadType)
        {
            if (TryGetRoadTypeMetadata(roadType, out var metadata))
                return metadata.DisplayName;

            return roadType;
        }

        internal static bool TryGetRoadTypeMetadata(string roadType, out RoadTypeMetadata metadata)
        {
            return RoadTypeMetadataByType.TryGetValue(roadType ?? string.Empty, out metadata);
        }

        private void BumpFieldVersion()
        {
            _fieldVersion++;
        }

        private List<IWidget> BuildRoadTypeCheckboxes()
        {
            return BuildRoadTypeCheckboxes(_importRoadTypeSelections, includeCounts: true, sortByCount: true);
        }

        private List<IWidget> BuildRoadTypeCheckboxes(Dictionary<string, bool> selections, bool includeCounts, bool sortByCount)
        {
            var rows = new List<IWidget>();
            _roadTypeTiles.Clear();

            var orderedRoadTypes = sortByCount
                ? _roadTypesOrder
                    .OrderByDescending(GetRoadTypeCount)
                    .ThenBy(GetRoadTypeDisplayName)
                    .ThenBy(roadType => _roadTypesOrder.IndexOf(roadType))
                    .ToList()
                : _roadTypesOrder.ToList();

            for (var i = 0; i < orderedRoadTypes.Count; i += 3)
            {
                var rowChildren = new List<IWidget>();
                rowChildren.Add(BuildRoadTypeCell(orderedRoadTypes[i], selections, includeCounts, sortByCount));

                if (i + 1 < orderedRoadTypes.Count)
                    rowChildren.Add(BuildRoadTypeCell(orderedRoadTypes[i + 1], selections, includeCounts, sortByCount));

                if (i + 2 < orderedRoadTypes.Count)
                    rowChildren.Add(BuildRoadTypeCell(orderedRoadTypes[i + 2], selections, includeCounts, sortByCount));

                rows.Add(CreateRow(rowChildren));
            }

            return rows;
        }

        private static IWidget CreateRow(List<IWidget> children)
        {
            return CreateRow(children, null);
        }

        private static IWidget CreateRow(List<IWidget> children, float? basis)
        {
            return CreateRow(children, basis, basis.HasValue ? 0f : (float?)null, basis.HasValue ? 0f : (float?)null);
        }

        private static IWidget CreateRow(List<IWidget> children, float? basis, float? grow, float? shrink)
        {
            var rowType = typeof(Game.UI.Widgets.Group).Assembly.GetType("Game.UI.Widgets.Row", throwOnError: false);
            if (rowType == null)
                return new Game.UI.Widgets.Group { children = children };

            var row = Activator.CreateInstance(rowType, true);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var childrenProp = rowType.GetProperty("children", flags);
            childrenProp?.SetValue(row, children);

            if (basis.HasValue || grow.HasValue || shrink.HasValue)
            {
                var flexProp = rowType.GetProperty("flex", flags);
                if (flexProp != null)
                {
                    var flexValue = Activator.CreateInstance(flexProp.PropertyType);
                    if (basis.HasValue)
                        flexProp.PropertyType.GetProperty("basis", flags)?.SetValue(flexValue, basis.Value);
                    if (grow.HasValue)
                        flexProp.PropertyType.GetProperty("grow", flags)?.SetValue(flexValue, grow.Value);
                    if (shrink.HasValue)
                        flexProp.PropertyType.GetProperty("shrink", flags)?.SetValue(flexValue, shrink.Value);
                    flexProp.SetValue(row, flexValue);
                }
            }

            var wrapProp = rowType.GetProperty("wrap", flags);
            if (wrapProp?.CanWrite == true)
                wrapProp.SetValue(row, false);

            return (IWidget)row;
        }

        private static IWidget CreateButtonPairRow(IWidget leftButton, IWidget rightButton)
        {
            TrySetWidgetFloatMember(leftButton, "width", 0.5f);
            TrySetWidgetFloatMember(rightButton, "width", 0.5f);
            return CreateRow(new List<IWidget>
            {
                leftButton,
                rightButton,
            });
        }

        private IWidget BuildStatusFooter(string statusText)
        {
            var statusIcon = new ImageField();
            SetWidgetStringMember(statusIcon, "m_URI", GetStatusIconPath(statusText));
            SetWidgetObjectMember(statusIcon, "m_Label", default(Game.UI.Localization.LocalizedString));
            SetWidgetBoolMember(statusIcon, "padding", false);
            SetWidgetFlex(statusIcon, null, 0f, 0f);

            var statusLabel = new Label
            {
                displayName = statusText,
            };
            SetWidgetBoolMember(statusLabel, "padding", false);
            TrySetWidgetFloatMember(statusLabel, "height", 0.018f);
            TrySetWidgetFloatMember(statusLabel, "minHeight", 0.018f);
            SetWidgetFlex(statusLabel, 0f, 1f, 0f);

            var row = CreateRow(new List<IWidget>
            {
                statusIcon,
                statusLabel,
            });

            SetWidgetBoolMember(row, "padding", false);
            TrySetWidgetFloatMember(row, "height", 0.024f);
            TrySetWidgetFloatMember(row, "minHeight", 0.024f);

            return row;
        }

        private string GetStatusIconPath(string statusText)
        {
            if (_importSystem == null)
                return "coui://ui-mods/images/StatusInfo.svg";

            var text = (statusText ?? string.Empty).ToLowerInvariant();

            if (text.Contains("failed") || text.Contains("invalid") || text.Contains("error"))
                return "Media/Game/Icons/AdvisorNotice.svg";

            if (_importSystem.IsDeletingSegments)
                return "Media/Game/Icons/Garbage.svg";

            if (_importSystem.IsImporting && (text.Contains("loading") || text.Contains("parsing") || text.Contains("preparing")))
                return "Media/Game/Icons/Roads.svg";

            if (_importSystem.IsImporting && (text.Contains("generating") || text.Contains("done") || text.Contains("waiting") || text.Contains(" | ")))
                return "Media/Game/Icons/RoadsServices.svg";

            if (_importSystem.HasGeneratedAssets || text.Contains("road generation complete"))
                return "Media/Game/Icons/Trophy.svg";

            if (_importSystem.HasLoadedData)
                return "coui://ui-mods/images/StatusInfo.svg";

            return "coui://ui-mods/images/StatusInfo.svg";
        }

        private static void SetWidgetStringMember(object widget, string memberName, string value)
        {
            if (widget == null)
                return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = widget.GetType();

            var property = type.GetProperty(memberName, flags);
            if (property != null && property.CanWrite && property.PropertyType == typeof(string))
            {
                property.SetValue(widget, value);
                return;
            }

            var field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(string))
                field.SetValue(widget, value);
        }

        private static void SetWidgetBoolMember(object widget, string memberName, bool value)
        {
            if (widget == null)
                return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = widget.GetType();

            var property = type.GetProperty(memberName, flags);
            if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
            {
                property.SetValue(widget, value);
                return;
            }

            var field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(bool))
                field.SetValue(widget, value);
        }

        private static void SetWidgetObjectMember(object widget, string memberName, object value)
        {
            if (widget == null)
                return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = widget.GetType();

            var property = type.GetProperty(memberName, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(widget, value);
                return;
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
                field.SetValue(widget, value);
        }

        private static void SetWidgetFlex(object widget, float? basis, float? grow, float? shrink)
        {
            if (widget == null)
                return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = widget.GetType();

            if (basis.HasValue)
                TrySetWidgetFloatMember(widget, "basis", basis.Value);
            if (grow.HasValue)
                TrySetWidgetFloatMember(widget, "grow", grow.Value);
            if (shrink.HasValue)
                TrySetWidgetFloatMember(widget, "shrink", shrink.Value);

            var flexProp = type.GetProperty("flex", flags);
            if (flexProp == null)
                return;

            var flexValue = Activator.CreateInstance(flexProp.PropertyType);
            if (basis.HasValue)
                flexProp.PropertyType.GetProperty("basis", flags)?.SetValue(flexValue, basis.Value);
            if (grow.HasValue)
                flexProp.PropertyType.GetProperty("grow", flags)?.SetValue(flexValue, grow.Value);
            if (shrink.HasValue)
                flexProp.PropertyType.GetProperty("shrink", flags)?.SetValue(flexValue, shrink.Value);
            flexProp.SetValue(widget, flexValue);
        }

        private static void TrySetWidgetFloatMember(object widget, string memberName, float value)
        {
            if (widget == null || string.IsNullOrWhiteSpace(memberName))
                return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = widget.GetType();
            var property = type.GetProperty(memberName, flags);
            if (property != null && property.CanWrite)
            {
                if (property.PropertyType == typeof(float))
                {
                    property.SetValue(widget, value);
                    return;
                }
            }

            var field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(float))
                field.SetValue(widget, value);
        }

        private IWidget BuildRoadTypeCell(string roadType)
        {
            return BuildRoadTypeCell(roadType, _importRoadTypeSelections, includeCounts: true, disableWhenCountZero: true);
        }

        private IWidget BuildRoadTypeCell(string roadType, Dictionary<string, bool> selections, bool includeCounts, bool disableWhenCountZero)
        {
            var button = new RoadTypeToggleButton(roadType, TryGetRoadTypeMetadata(roadType, out var metadata) ? metadata : null)
            {
                Count = GetRoadTypeCount(roadType),
            };

            button.NameLabel = new Label
            {
                displayName = includeCounts ? FormatRoadTypeLabel(button.DisplayName, button.Count) : button.DisplayName,
            };

            var root = CreateRow(new List<IWidget>
            {
                BuildRoadTypeToggle(roadType, selections, disableWhenCountZero),
                button.NameLabel,
            }, null);

            button.Root = root;
            _roadTypeTiles.Add(button);

            return root;
        }

        private ToggleField BuildRoadTypeToggle(string roadType)
        {
            return BuildRoadTypeToggle(roadType, _importRoadTypeSelections, disableWhenCountZero: true);
        }

        private ToggleField BuildRoadTypeToggle(string roadType, Dictionary<string, bool> selections, bool disableWhenCountZero)
        {
            if (!selections.ContainsKey(roadType))
                selections[roadType] = true;

            var key = roadType;
            var count = GetRoadTypeCount(roadType);
            return new ToggleField
            {
                displayName = string.Empty,
                accessor = new TypedValueAccessor<bool>(
                    () => selections.TryGetValue(key, out var value) && value,
                    value => SetRoadTypeSelection(selections, key, value)),
                valueVersion = () => _fieldVersion,
                disabled = () => _importSystem != null && (_importSystem.IsImporting || (disableWhenCountZero && count <= 0)),
            };
        }

        private ToggleField BuildGeneratePropertyToggle(string labelKey, string labelFallback, Func<bool> getter, Action<bool> setter)
        {
            return new ToggleField
            {
                displayName = OsmLocalization.T(labelKey, labelFallback),
                accessor = new TypedValueAccessor<bool>(getter, setter),
                valueVersion = () => _fieldVersion,
                disabled = () => _importSystem != null && (_importSystem.IsImporting || !_importSystem.HasLoadedData),
            };
        }

        internal sealed class RoadTypeMetadata
        {
            public RoadTypeMetadata(string displayNameKey, string displayNameFallback, string descriptionKey, string descriptionFallback, string iconCandidate)
            {
                DisplayNameKey = displayNameKey;
                DisplayNameFallback = displayNameFallback;
                DescriptionKey = descriptionKey;
                DescriptionFallback = descriptionFallback;
                IconCandidate = iconCandidate ?? string.Empty;
            }

            public string DisplayNameKey { get; }

            public string DisplayNameFallback { get; }

            public string DescriptionKey { get; }

            public string DescriptionFallback { get; }

            public string IconCandidate { get; }

            public string DisplayName => OsmLocalization.T(DisplayNameKey, DisplayNameFallback);

            public string Description => OsmLocalization.T(DescriptionKey, DescriptionFallback);
        }

        private sealed class RoadTypeFilterAdapter : FilterMenu.IAdapter
        {
            private readonly OsmImportPanelSystem _panel;
            private readonly Dictionary<string, bool> _selections;
            private readonly Dictionary<string, string> _displayToRoadType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly bool _includeCounts;

            public RoadTypeFilterAdapter(OsmImportPanelSystem panel, Dictionary<string, bool> selections, bool includeCounts = false)
            {
                _panel = panel;
                _selections = selections;
                _includeCounts = includeCounts;
            }

            public List<string> availableFilters { get; } = new List<string>();

            public List<string> activeFilters { get; } = new List<string>();

            public Action onAvailableFiltersChanged { get; set; }

            public void SyncAvailableRoadTypes(IEnumerable<string> roadTypes)
            {
                availableFilters.Clear();
                _displayToRoadType.Clear();

                foreach (var roadType in roadTypes ?? Array.Empty<string>())
                {
                    var displayName = FormatDisplayNameWithCount(roadType);
                    if (_displayToRoadType.ContainsKey(displayName))
                        continue;

                    _displayToRoadType[displayName] = roadType;
                    availableFilters.Add(displayName);
                }

                activeFilters.RemoveAll(filter => !_displayToRoadType.ContainsKey(filter));
                onAvailableFiltersChanged?.Invoke();
            }

            public void SyncActiveRoadTypes(IEnumerable<string> roadTypes)
            {
                activeFilters.Clear();

                foreach (var roadType in roadTypes ?? Array.Empty<string>())
                {
                    var displayName = FormatDisplayNameWithCount(roadType);
                    if (!_displayToRoadType.ContainsKey(displayName) || activeFilters.Contains(displayName))
                        continue;

                    activeFilters.Add(displayName);
                }
            }

            private string FormatDisplayNameWithCount(string roadType)
            {
                var displayName = OsmImportPanelSystem.GetRoadTypeDisplayName(roadType);
                if (_includeCounts
                    && _panel._importSystem?.RoadTypeCountsByType != null
                    && _panel._importSystem.RoadTypeCountsByType.TryGetValue(roadType ?? string.Empty, out var count)
                    && count > 0)
                {
                    return FormatRoadTypeLabel(displayName, count);
                }
                return displayName;
            }

            public void ToggleFilter(string filter, bool active)
            {
                if (!_displayToRoadType.TryGetValue(filter ?? string.Empty, out var roadType))
                    return;

                _panel.SetRoadTypeSelection(_selections, roadType, active);
                SyncActiveRoadTypes(_panel.GetSelectedRoadTypes(_selections));
            }

            public void ClearFilters()
            {
                foreach (var roadType in _displayToRoadType.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                    _selections[roadType] = false;

                _panel.BumpFieldVersion();
                _panel.SyncRoadTypeFilterAdapters();
                _panel.RefreshSelectedRoadTypes();
                SyncActiveRoadTypes(Array.Empty<string>());
            }
        }

        private sealed class RoadTypeToggleButton
        {
            public RoadTypeToggleButton(string roadType, RoadTypeMetadata metadata)
            {
                RoadType = roadType;
                Metadata = metadata;
            }

            public string RoadType { get; }

            public RoadTypeMetadata Metadata { get; }

            public string DisplayName => Metadata?.DisplayName ?? GetRoadTypeDisplayName(RoadType);

            public IWidget Root { get; set; }

            public IWidget ContentColumn { get; set; }

            public Label NameLabel { get; set; }

            public Label CountLabel { get; set; }

            public int Count { get; set; }
        }

        private static string GetDefaultOsmPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
                return string.Empty;

            return Path.Combine(userProfile, "Downloads") + Path.DirectorySeparatorChar;
        }

        private sealed class TypedValueAccessor<T> : ITypedValueAccessor<T>
        {
            private readonly Func<T> _get;
            private readonly Action<T> _set;

            public TypedValueAccessor(Func<T> get, Action<T> set)
            {
                _get = get;
                _set = set;
            }

            public T GetTypedValue() => _get();

            public void SetTypedValue(T value) => _set(value);

            public Type valueType => typeof(T);

            public IValueAccessor parent => null;

            public object GetValue() => GetTypedValue();

            public void SetValue(object value)
            {
                if (value is T typed)
                {
                    SetTypedValue(typed);
                    return;
                }

                if (typeof(T) == typeof(string))
                {
                    SetTypedValue((T)(object)(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
                    return;
                }

                if (typeof(T) == typeof(double))
                {
                    SetTypedValue((T)(object)Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    return;
                }

                if (typeof(T) == typeof(int))
                {
                    SetTypedValue((T)(object)Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    return;
                }

                if (typeof(T) == typeof(bool))
                {
                    SetTypedValue((T)(object)Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                }
            }
        }

    }
}
