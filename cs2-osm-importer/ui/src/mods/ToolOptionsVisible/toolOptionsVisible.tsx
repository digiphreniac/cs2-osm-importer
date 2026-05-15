import { ModuleRegistryExtend } from "cs2/modding";
import { tool } from "cs2/bindings";

const toolId = "OsmImportTool";

export const ToolOptionsVisibility: ModuleRegistryExtend = (Component: any) => {
  return () => Component() || tool.activeTool$.value.id == toolId;
};
