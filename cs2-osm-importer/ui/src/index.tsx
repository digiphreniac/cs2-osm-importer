// docs: https://cs2.paradoxwikis.com/UI_Modding

import "./assets/Openstreetmap_logo.png";
import { ModRegistrar } from "cs2/modding";
import { OsmToolOptions } from "mods/OsmToolOptions";
import { ToolOptionsVisibility } from "mods/ToolOptionsVisible/toolOptionsVisible";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.extend(
        "game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
        "MouseToolOptions",
        OsmToolOptions,
    );

    moduleRegistry.extend(
        "game-ui/game/components/tool-options/tool-options-panel.tsx",
        "useToolOptionsVisible",
        ToolOptionsVisibility,
    );
}

export default register;
