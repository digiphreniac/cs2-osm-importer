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