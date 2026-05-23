using System.Collections.Generic;
using Colossal.UI.Binding;
using Game.UI.Editor;
using Game.UI.Widgets;

namespace cs2_osm_importer
{
    // Minimal EditorPanel scaffold for the new "Load OSM Zoning data" UI.
    public partial class OsmZoningPanelSystem : EditorPanelSystemBase
    {
        private string _pathValue = string.Empty;
        private PopupValueField<string> _pathField;
        private Button _loadButton;
        private OsmZoningImportSystem _importSystem;
        private Label _featureCountLabel;
        private Button _applyButton;
        private int _lastFeatureCount = -1;
n        protected override void OnCreate()
        {
            base.OnCreate();
            _importSystem = World.GetOrCreateSystemManaged<OsmZoningImportSystem>();
n            title = "OSM Zoning Import";
            uiTag = "UITagPrefab:OSMZoningPanel";
            children = BuildChildren();
        }
n        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (_featureCountLabel != null && _importSystem != null)
            {
                var count = _importSystem.GetPreviewFeatureCount();
                _featureCountLabel.displayName = $"Parsed features: {count}";
                if (count != _lastFeatureCount)
                {
                    _lastFeatureCount = count;
                    // Rebuild children to show the feature list and overrides when preview changes.
                    children = BuildChildren();
                }
            }
        }
n        private void SetPathValue(string v) => _pathValue = v;
n        private List<IWidget> BuildChildren()
        {
            var content = new List<IWidget>();
n            content.Add(new Label { displayName = "OSM Zoning Import Tool" });
            content.Add(new Divider());
n            _pathField = new PopupValueField<string>
            {
                displayName = "OSM file path",
                tooltip = "Pick an OSM file (.osm) containing zoning/landuse data.",
                accessor = new TypedValueAccessor<string>(() => _pathValue, SetPathValue),
                icon = "Media/Editor/Folder.svg",
            };
            _pathField.popup = new FilePickerPopup(new[] { ".osm" }, () => _pathField.expanded = false);
            content.Add(_pathField);
n            _loadButton = new Button { displayName = "Load OSM Zoning data" };
            _loadButton.triggered = () => _importSystem.LoadZoningFromFile(_pathValue);
            content.Add(_loadButton);
n            content.Add(new Divider());
            _featureCountLabel = new Label { displayName = "Parsed features: 0" };
            content.Add(_featureCountLabel);
n            // Feature list with density override controls (limited to first 200 features for performance)
            var features = _importSystem?.GetPreviewFeatures() ?? new List<OsmZoningFeature>();
            var maxDisplay = 200;
            int displayed = 0;
            foreach (var f in features)
            {
                if (displayed++ >= maxDisplay) break;
                content.Add(new Label { displayName = $"[{f.Id}] {f.Type} - { (string.IsNullOrEmpty(f.Name) ? "(no name)" : f.Name) }" });
                var currentOverride = _importSystem.GetFeatureDensityOverride(f.Id);
                var current = currentOverride.HasValue ? currentOverride.Value.ToString() : "Auto";
                var popup = new PopupValueField<string>
                {
                    displayName = "Density override",
                    accessor = new TypedValueAccessor<string>(() => current, v => {
                        // parse v and set override
                        if (string.Equals(v, "Auto", System.StringComparison.OrdinalIgnoreCase))
                            _importSystem.SetFeatureDensityOverride(f.Id, null);
                        else if (System.Enum.TryParse<DensityLevel>(v, out var parsed))
                            _importSystem.SetFeatureDensityOverride(f.Id, parsed);
                    }),
                    items = new[] { "Auto", "Low", "Medium", "High" }
                };
                content.Add(popup);
            }
n            if (features.Count > maxDisplay)
                content.Add(new Label { displayName = $"... {features.Count - maxDisplay} more features not shown" });
n            content.Add(new Label { displayName = "Apply zoning is Play-mode only. Use 'Apply Zoning' while the city is running." });
            _applyButton = new Button { displayName = "Apply Zoning" };
            _applyButton.tooltip = "Requires Play mode; Apply parsed zoning to the running city.";
            _applyButton.triggered = () => _importSystem.ApplyZoning();
            content.Add(_applyButton);
n            content.Add(new Divider());
            content.Add(new Label { displayName = "Density inference will be suggested; overrides available in Play mode." });
n            return content;
        }
    }
}