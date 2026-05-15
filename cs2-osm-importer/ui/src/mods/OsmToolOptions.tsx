import { ModuleRegistryExtend } from "cs2/modding";

export const OsmToolOptions: ModuleRegistryExtend = (_Component: any) => {
  return (props: any) => _Component(props);
};
