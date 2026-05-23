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
n        protected override void OnCreate()
        {
            base.OnCreate();
            _importSystem = World.GetOrCreateSystemManaged<OsmZoningImportSystem>();
n            title = "OSM Zoning Import";
            uiTag = "UITagPrefab:OSMZoningPanel";
            children = BuildChildren();
        }
n        private void SetPathValue(string v) => _pathValue = v;
n        private List<IWidget> BuildChildren()
        {
            var content = new List<IWidget>();
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
            content.Add(new Label { displayName = "Density inference will be suggested; overrides available in Play mode." });
n            return content;
        }
    }
}
