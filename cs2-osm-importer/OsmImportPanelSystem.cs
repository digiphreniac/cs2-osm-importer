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
        };

        internal static readonly Dictionary<string, RoadTypeMetadata> RoadTypeMetadataByType =
            new Dictionary<string, RoadTypeMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                { "motorway", new RoadTypeMetadata("Motorway", "High-capacity, access-controlled road for long-distance traffic.", "Media/Game/Icons/Highways.svg") },
                { "trunk", new RoadTypeMetadata("Trunk", "Major intercity road that carries important through traffic.", "Media/Game/Icons/HighwayTwoway4lanes.svg") },
                { "primary", new RoadTypeMetadata("Primary", "Main road connecting important towns, districts, and hubs.", "Media/Game/Icons/MediumRoadDivided.svg") },
                { "secondary", new RoadTypeMetadata("Secondary", "Significant connector between smaller towns and city districts.", "Media/Game/Icons/MediumRoad.svg") },
                { "tertiary", new RoadTypeMetadata("Tertiary", "Local connector that feeds traffic into higher-order roads.", "Media/Game/Icons/SmallRoads.svg") },
                { "unclassified", new RoadTypeMetadata("Unclassified", "Minor public road used for general local access.", "Media/Game/Icons/SmallRoad.svg") },
                { "residential", new RoadTypeMetadata("Residential", "Neighborhood street serving homes and local access.", "Media/Game/Icons/SmallRoadAsymmetric.svg") },
                { "service", new RoadTypeMetadata("Service", "Access road for buildings, parking, depots, and utilities.", "Media/Game/Icons/RoadsServices.svg") },
                { "living_street", new RoadTypeMetadata("Living street", "Low-speed shared street where pedestrians have priority.", "Media/Game/Icons/PedestrianStreet.svg") },
                { "road", new RoadTypeMetadata("Road", "Generic road tag used when no more specific type is known.", "Media/Game/Icons/Roads.svg") },
                { "motorway_link", new RoadTypeMetadata("Motorway link", "Ramp or connector linking a motorway to nearby roads.", "Media/Game/Icons/HighwayOneway2lanes.svg") },
                { "trunk_link", new RoadTypeMetadata("Trunk link", "Connector road feeding traffic into or out of a trunk route.", "Media/Game/Icons/HighwayOneway2lanes.svg") },
                { "primary_link", new RoadTypeMetadata("Primary link", "Connector between primary roads and the wider network.", "Media/Game/Icons/MediumRoadOneway.svg") },
                { "secondary_link", new RoadTypeMetadata("Secondary link", "Connector between secondary roads and local streets.", "Media/Game/Icons/SmallRoad.svg") },
                { "tertiary_link", new RoadTypeMetadata("Tertiary link", "Connector between tertiary roads and nearby local access.", "Media/Game/Icons/SmallRoadOneway1lane.svg") },
            };

        private OsmImportSystem _importSystem;
        private Label _statusLabel;
        private Button _loadButton;
        private Button _resetDefaultsButton;
        private Button _selectAllTypesButton;
        private Button _deselectAllTypesButton;
        private Button _generateButton;
        private Button _deleteButton;
        private Game.UI.Widgets.Group _loadConfirmationGroup;
        private Button _confirmLoadButton;
        private Button _cancelLoadButton;
        private PopupValueField<string> _pathField;
        private EditorSection _importDataSection;
        private EditorSection _generateRoadsSection;
        private Game.UI.Widgets.Group _roadTypesCheckboxGroup;
        private Game.UI.Widgets.Group _roadTypesGroup;
        private FloatSliderField _simplifyToleranceField;
        private FloatSliderField _curveToleranceField;
        private FloatSliderField _scaleFactorField;
        private IntSliderField _maxWaysField;
        private EditorSection _advancedSettingsSection;
        private readonly Dictionary<string, bool> _roadTypeSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<RoadTypeToggleButton> _roadTypeTiles = new List<RoadTypeToggleButton>();
        private readonly List<string> _roadTypesOrder = new List<string>(SupportedRoadTypes);
        private string _lastRoadTypeCountsJson = string.Empty;
        private string _pathValue;
        private double _simplifyToleranceValue = 1d;
        private double _curveToleranceValue = 0.5d;
        private double _scaleFactorValue = 1d;
        private int _maxWaysValue = 10000;
        private string _statusText = "Ready.";
        private int _fieldVersion;
        private bool _wasPanelActive;
        private bool _showLoadConfirmation;

        protected override void OnCreate()
        {
            base.OnCreate();
            _importSystem = World.GetOrCreateSystemManaged<OsmImportSystem>();

            title = "OSM Importer Tool";
            uiTag = "UITagPrefab:OSMImportPanel";
            children = BuildChildren();

            SetPathValue(GetDefaultOsmPath());
            SetSimplifyToleranceValue(1d);
            SetCurveToleranceValue(0.5d);
            SetScaleFactorValue(1d);
            SetMaxWaysValue(10000);
            SelectAllRoadTypes();
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
                UpdateRoadTypeCounts();
                UpdateStatus(_importSystem.Status);
                UpdateControlStates();
            }

        }

        private List<IWidget> BuildChildren()
        {
            var content = new List<IWidget>();

            content.Add(new Label
            {
                displayName = "OSM Import Tool",
            });

            content.Add(new Divider());

            _statusLabel = new Label
            {
                displayName = _statusText,
            };

            _pathField = new PopupValueField<string>
            {
                displayName = "OSM file path",
                accessor = new TypedValueAccessor<string>(() => _pathValue, SetPathValue),
                icon = "Media/Editor/Folder.svg",
            };
            _pathField.popup = new FilePickerPopup(new[] { ".osm" }, () => _pathField.expanded = false);

            _simplifyToleranceField = new FloatSliderField
            {
                displayName = "Simplify tolerance (m)",
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
                displayName = "Curve tolerance (m)",
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
                displayName = "Scale factor",
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
                displayName = "Max ways",
                min = 1000,
                max = 100000,
                step = 1000,
                stepMultiplier = 10,
                separateThousands = true,
                accessor = new TypedValueAccessor<int>(() => _maxWaysValue, SetMaxWaysValue),
                valueVersion = () => _fieldVersion,
            };

            _advancedSettingsSection = new EditorSection
            {
                displayName = "Advanced settings",
                expanded = true,
                children = new List<IWidget>
                {
                    _simplifyToleranceField,
                    _curveToleranceField,
                    _scaleFactorField,
                    _maxWaysField,
                },
            };

            _loadButton = new Button
            {
                displayName = "Load OSM Data",
                action = PromptLoadOsmDataConfirmation,
            };

            _confirmLoadButton = new Button
            {
                displayName = "Continue",
                action = ConfirmLoadOsmData,
            };

            _cancelLoadButton = new Button
            {
                displayName = "Cancel",
                action = CancelLoadOsmDataConfirmation,
            };

            _loadConfirmationGroup = new Game.UI.Widgets.Group
            {
                displayName = "Confirm OSM import",
                hidden = () => !_showLoadConfirmation,
                children = new List<IWidget>
                {
                    new Label
                    {
                        displayName = "Loading OSM data may temporarily freeze the game while the file is parsed and road definitions are generated.",
                        level = Label.Level.SubTitle,
                    },
                    _confirmLoadButton,
                    _cancelLoadButton,
                },
            };

            _resetDefaultsButton = new Button
            {
                displayName = "Reset to defaults",
                action = ResetDefaults,
            };

            _importDataSection = new EditorSection
            {
                displayName = "Import data",
                expanded = true,
                children = new List<IWidget>
                {
                    new Label
                    {
                        displayName = "Pick an OSM input file and configure how the data is parsed before loading it into the game.",
                    },
                    _pathField,
                    _advancedSettingsSection,
                    _loadButton,
                    _loadConfirmationGroup,
                    _resetDefaultsButton,
                },
            };

            content.Add(_importDataSection);

            _generateButton = new Button
            {
                displayName = "Generate Roads",
                action = GenerateRoads,
            };

            _deleteButton = new Button
            {
                displayName = "Delete All Roads",
                action = DeleteAllRoads,
            };

            _selectAllTypesButton = new Button
            {
                displayName = "Select all",
                action = SelectAllRoadTypes,
            };

            _deselectAllTypesButton = new Button
            {
                displayName = "Deselect all",
                action = DeselectAllRoadTypes,
            };

            _roadTypesCheckboxGroup = new Game.UI.Widgets.Group
            {
                displayName = "Road types",
                children = BuildRoadTypeCheckboxes(),
            };

            _roadTypesGroup = new Game.UI.Widgets.Group
            {
                displayName = "Generate Roads",
                children = new List<IWidget>
                {
                    new Label
                    {
                        displayName = "Choose which loaded road types should be generated.",
                        level = Label.Level.SubTitle
                    },
                    _roadTypesCheckboxGroup,
                    new Label
                    {
                        displayName = $"Supported types: {string.Join(", ", SupportedRoadTypes)}",
                        level = Label.Level.SubTitle,
                    },
                    // CreateButtonPairRow(_selectAllTypesButton, _deselectAllTypesButton),
                    _selectAllTypesButton,
                    _deselectAllTypesButton,
                    _generateButton,
                    _deleteButton,
                },
            };

            content.Add(_roadTypesGroup);

            content.Add(_statusLabel);

            return new List<IWidget>
            {
                new Scrollable
                {
                    direction = Direction.Vertical,
                    children = content,
                },
            };
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
                MaxWays = _maxWaysValue,
            };

            if (!_importSystem.LoadOsmData(options))
            {
                UpdateStatus("An OSM import is already running.");
                return;
            }

            UpdateStatus(_importSystem.Status);
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
            var options = new OsmGenerateOptions();
            foreach (var roadType in GetSelectedRoadTypes())
            {
                options.HighwayTypes.Add(roadType);
            }

            if (!_importSystem.GenerateRoads(options))
            {
                UpdateStatus("An OSM operation is already running.");
                return;
            }

            UpdateStatus(_importSystem.Status);
        }

        private void DeleteAllRoads()
        {
            if (!_importSystem.DeleteAllRoads())
            {
                UpdateStatus("An OSM operation is already running.");
                return;
            }

            UpdateStatus(_importSystem.Status);
        }

        private void ResetDefaults()
        {
            SetPathValue(GetDefaultOsmPath());
            SetSimplifyToleranceValue(1d);
            SetCurveToleranceValue(0.5d);
            SetScaleFactorValue(1d);
            SetMaxWaysValue(10000);
            SelectAllRoadTypes();
            ResetAdvancedSettingsSection();
            UpdateStatus("Defaults restored.");
        }

        private void SelectAllRoadTypes()
        {
            foreach (var roadType in SupportedRoadTypes)
                _roadTypeSelections[roadType] = true;

            BumpFieldVersion();
            UpdateStatus("All supported road types selected.");
        }

        private void DeselectAllRoadTypes()
        {
            foreach (var roadType in SupportedRoadTypes)
                _roadTypeSelections[roadType] = false;

            BumpFieldVersion();
            UpdateStatus("All supported road types deselected.");
        }

        private void ResetAdvancedSettingsSection()
        {
            if (_advancedSettingsSection != null)
                _advancedSettingsSection.expanded = true;
        }

        private void SetRoadTypeSelection(string roadType, bool selected)
        {
            if (string.IsNullOrWhiteSpace(roadType))
                return;

            _roadTypeSelections[roadType] = selected;
            BumpFieldVersion();
        }

        private IEnumerable<string> GetSelectedRoadTypes()
        {
            return _roadTypesOrder.Where(roadType => _roadTypeSelections.TryGetValue(roadType, out var selected) && selected);
        }

        private string GetSelectedRoadTypesSummary()
        {
            var selected = GetSelectedRoadTypes().ToList();
            if (selected.Count == 0)
                return "none";

            if (selected.Count == _roadTypesOrder.Count)
                return "all supported types";

            if (selected.Count <= 3)
                return string.Join(", ", selected);

            return $"{selected.Count} selected";
        }

        private void UpdateStatus(string status)
        {
            _statusText = string.IsNullOrWhiteSpace(status) ? "Ready." : status;
            if (_statusLabel != null)
            {
                _statusLabel.displayName = _statusText;
            }
        }

        private void UpdateControlStates()
        {
            var isBusy = _importSystem.IsImporting;
            var hasLoadedData = _importSystem.HasLoadedData;
            var hasGeneratedAssets = _importSystem.HasGeneratedAssets;

            if (_loadButton != null)
                _loadButton.disabled = () => isBusy || hasGeneratedAssets;

            if (_resetDefaultsButton != null)
                _resetDefaultsButton.disabled = () => isBusy;

            if (_selectAllTypesButton != null)
                _selectAllTypesButton.disabled = () => isBusy;

            if (_deselectAllTypesButton != null)
                _deselectAllTypesButton.disabled = () => isBusy;

            if (_generateButton != null)
                _generateButton.disabled = () => isBusy || !hasLoadedData;

            if (_deleteButton != null)
                _deleteButton.disabled = () => !hasGeneratedAssets;

            if (_roadTypesGroup != null)
                _roadTypesGroup.hidden = () => !hasLoadedData;
        }

        private void SetPathValue(string value)
        {
            _pathValue = value ?? string.Empty;
            BumpFieldVersion();
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
            if (countsJson == _lastRoadTypeCountsJson && _roadTypeTiles.Count > 0)
                return;

            _lastRoadTypeCountsJson = countsJson;
            RefreshSelectedRoadTypes();
        }

        private int GetRoadTypeCount(string roadType)
        {
            if (_importSystem?.RoadTypeCountsByType != null && _importSystem.RoadTypeCountsByType.TryGetValue(roadType ?? string.Empty, out var count))
                return count;

            return 0;
        }

        private static string FormatRoadTypeLabel(string displayName, int count)
        {
            return $"{displayName} ({count})";
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
            var rows = new List<IWidget>();
            _roadTypeTiles.Clear();

            for (var i = 0; i < _roadTypesOrder.Count; i += 3)
            {
                var rowChildren = new List<IWidget>();
                rowChildren.Add(BuildRoadTypeCell(_roadTypesOrder[i]));

                if (i + 1 < _roadTypesOrder.Count)
                    rowChildren.Add(BuildRoadTypeCell(_roadTypesOrder[i + 1]));

                if (i + 2 < _roadTypesOrder.Count)
                    rowChildren.Add(BuildRoadTypeCell(_roadTypesOrder[i + 2]));

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
            var button = new RoadTypeToggleButton(roadType, TryGetRoadTypeMetadata(roadType, out var metadata) ? metadata : null)
            {
                Count = GetRoadTypeCount(roadType),
            };

            button.NameLabel = new Label
            {
                displayName = FormatRoadTypeLabel(button.DisplayName, button.Count),
            };

            var root = CreateRow(new List<IWidget>
            {
                BuildRoadTypeToggle(roadType),
                button.NameLabel,
            }, null);

            button.Root = root;
            _roadTypeTiles.Add(button);

            return root;
        }

        private ToggleField BuildRoadTypeToggle(string roadType)
        {
            if (!_roadTypeSelections.ContainsKey(roadType))
                _roadTypeSelections[roadType] = true;

            var key = roadType;
            return new ToggleField
            {
                displayName = string.Empty,
                accessor = new TypedValueAccessor<bool>(
                    () => _roadTypeSelections.TryGetValue(key, out var value) && value,
                    value => SetRoadTypeSelection(key, value)),
                valueVersion = () => _fieldVersion,
            };
        }

        internal sealed class RoadTypeMetadata
        {
            public RoadTypeMetadata(string displayName, string description, string iconCandidate)
            {
                DisplayName = displayName;
                Description = description;
                IconCandidate = iconCandidate ?? string.Empty;
            }

            public string DisplayName { get; }

            public string Description { get; }

            public string IconCandidate { get; }
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

            public string DisplayName => Metadata?.DisplayName ?? RoadType;

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
