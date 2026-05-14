using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Game.UI.Editor;
using Game.UI.Editor.Widgets;
using Game.Reflection;
using Game.UI.Localization;
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

        private OsmImportSystem _importSystem;
        private Label _statusLabel;
        private Group _importGroup;
        private Group _roadTypesGroup;
        private Button _loadButton;
        private Button _resetDefaultsButton;
        private Button _selectAllTypesButton;
        private Button _generateButton;
        private Button _deleteButton;
        private PopupValueField<string> _pathField;
        private FloatSliderField _simplifyToleranceField;
        private FloatSliderField _curveToleranceField;
        private FloatSliderField _scaleFactorField;
        private IntSliderField _maxWaysField;
        private EditorSection _advancedSettingsSection;
        private Group _roadTypesCheckboxGroup;
        private readonly Dictionary<string, bool> _roadTypeSelections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _roadTypesOrder = new List<string>(SupportedRoadTypes);
        private string _pathValue;
        private double _simplifyToleranceValue = 1d;
        private double _curveToleranceValue = 0.5d;
        private double _scaleFactorValue = 1d;
        private int _maxWaysValue = 10000;
        private string _statusText = "Ready.";
        private int _fieldVersion;
        private bool _wasPanelActive;

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

            _statusLabel = new Label
            {
                displayName = _statusText,
            };
            content.Add(_statusLabel);
            content.Add(new Divider());

            _pathField = new PopupValueField<string>
            {
                displayName = "OSM file path",
                accessor = new TypedValueAccessor<string>(() => _pathValue, SetPathValue),
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
                action = LoadOsmData,
            };

            _resetDefaultsButton = new Button
            {
                displayName = "Reset to defaults",
                action = ResetDefaults,
            };

            _importGroup = new Group
            {
                displayName = "Import data",
                children = new List<IWidget>
                {
                    new Label
                    {
                        displayName = "Pick an OSM input file and configure how the data is parsed before loading it into the game.",
                    },
                    _pathField,
                    _advancedSettingsSection,
                    _loadButton,
                    _resetDefaultsButton,
                },
            };

            content.Add(_importGroup);
            content.Add(new Divider());

            _selectAllTypesButton = new Button
            {
                displayName = "Select all types",
                action = SelectAllRoadTypes,
            };

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

            _roadTypesCheckboxGroup = new Group
            {
                displayName = "Road types",
                children = BuildRoadTypeCheckboxes(),
            };

            _roadTypesGroup = new Group
            {
                displayName = "Generate roads",
                children = new List<IWidget>
                {
                    new Label
                    {
                        displayName = "Choose which loaded road types should be generated.",
                    },
                    _roadTypesCheckboxGroup,
                    new Label
                    {
                        displayName = $"Supported types: {string.Join(", ", SupportedRoadTypes)}",
                    },
                    _selectAllTypesButton,
                    _generateButton,
                    _deleteButton,
                },
            };

            content.Add(_roadTypesGroup);

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

        private void ResetAdvancedSettingsSection()
        {
            if (_advancedSettingsSection != null)
                _advancedSettingsSection.expanded = true;
        }

        private List<IWidget> BuildRoadTypeCheckboxes()
        {
            var rows = new List<IWidget>();

            for (var i = 0; i < _roadTypesOrder.Count; i += 2)
            {
                var rowChildren = new List<IWidget>();
                rowChildren.Add(BuildRoadTypeCell(_roadTypesOrder[i]));

                if (i + 1 < _roadTypesOrder.Count)
                    rowChildren.Add(BuildRoadTypeCell(_roadTypesOrder[i + 1]));

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
            var rowType = typeof(Group).Assembly.GetType("Game.UI.Widgets.Row", throwOnError: false);
            if (rowType == null)
                return new Group { children = children };

            var row = Activator.CreateInstance(rowType, true);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var childrenProp = rowType.GetProperty("children", flags);
            childrenProp?.SetValue(row, children);

            if (basis.HasValue)
            {
                var flexProp = rowType.GetProperty("flex", flags);
                if (flexProp != null)
                {
                    var flexValue = Activator.CreateInstance(flexProp.PropertyType);
                    flexProp.PropertyType.GetProperty("basis", flags)?.SetValue(flexValue, basis.Value);
                    flexProp.PropertyType.GetProperty("grow", flags)?.SetValue(flexValue, 0f);
                    flexProp.PropertyType.GetProperty("shrink", flags)?.SetValue(flexValue, 0f);
                    flexProp.SetValue(row, flexValue);
                }
            }

            var wrapProp = rowType.GetProperty("wrap", flags);
            if (wrapProp?.CanWrite == true)
                wrapProp.SetValue(row, false);

            return (IWidget)row;
        }

        private IWidget BuildRoadTypeCell(string roadType)
        {
            return CreateRow(new List<IWidget>
            {
                BuildRoadTypeToggle(roadType),
                new Label
                {
                    displayName = roadType,
                },
            });
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

        private void BumpFieldVersion()
        {
            _fieldVersion++;
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
