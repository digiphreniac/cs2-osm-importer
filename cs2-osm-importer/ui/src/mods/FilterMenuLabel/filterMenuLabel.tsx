import React, { useCallback, useMemo, useState } from "react";
import { Localized } from "cs2/l10n";
import type { LocElement } from "cs2/l10n";
import type { ModuleRegistry } from "cs2/modding";
import { Tooltip } from "cs2/ui";

type WidgetPath = {
    group: string;
    path: unknown[];
};

type BoundFilterMenuProps = {
    parent: WidgetPath;
    path: unknown;
    props: {
        availableFilters: string[];
        activeFilters: string[];
        displayName?: string | LocElement;
        tooltip?: LocElement;
    };
};

type FilterMenuProps = {
    availableFilters: string[];
    activeFilters: string[];
    label?: string | LocElement;
    tooltip?: LocElement;
    onToggleFilter: (filter: string, active: boolean) => void;
    onClear: () => void;
};

const resolvePath = (parent: WidgetPath, path: unknown): WidgetPath => ({
    group: parent.group,
    path: [...(parent.path ?? []), ...(Array.isArray(path) ? path : [path])],
});

const renderLabel = (value: string | LocElement | undefined, fallback: string) => {
    if (!value) {
        return fallback;
    }

    return typeof value === "string" ? value : <Localized value={value} />;
};

const renderTooltip = (tooltip: LocElement | undefined) => tooltip ? <Localized value={tooltip} /> : undefined;

export const registerFilterMenuLabel = (moduleRegistry: ModuleRegistry) => {
    const PopupValueField = moduleRegistry.get("game-ui/editor/widgets/fields/popup-value-field.tsx", "PopupValueField");
    const ToggleField = moduleRegistry.get("game-ui/editor/widgets/fields/toggle-field.tsx", "ToggleField");
    const EditorButton = moduleRegistry.get("game-ui/editor/widgets/button/button.tsx", "EditorButton");
    const Scrollable = moduleRegistry.get("game-ui/common/scrolling/scrollable.tsx", "Scrollable");
    const filterMenuTheme = moduleRegistry.get("game-ui/editor/widgets/filter-menu/filter-menu.module.scss", "classes");
    const Loc = moduleRegistry.get("game-ui/common/localization/loc.generated.ts", "Loc");
    const EditorWidgetType = moduleRegistry.get("game-ui/editor/data-binding/editor-panel-bindings.ts", "EditorWidgetType");
    const toggleFilter = moduleRegistry.get("game-ui/editor/data-binding/editor-panel-bindings.ts", "toggleFilter");
    const clearFilters = moduleRegistry.get("game-ui/editor/data-binding/editor-panel-bindings.ts", "clearFilters");
    const editorWidgetComponents = moduleRegistry.get("game-ui/editor/widgets/editor-widget-renderer.tsx", "editorWidgetComponents");

    const FilterMenu = ({ availableFilters, activeFilters, label, tooltip, onToggleFilter, onClear }: FilterMenuProps) => {
        const [expanded, setExpanded] = useState(false);
        const filters = useMemo(
            () => availableFilters.map((filter) => ({ filter, active: activeFilters.includes(filter) })),
            [activeFilters, availableFilters],
        );
        const value = useMemo(
            () => activeFilters.length > 0 ? activeFilters.join(", ") : <Loc.Common.NONE />,
            [activeFilters],
        );

        const field = (
            <PopupValueField
                label={renderLabel(label, "Filters")}
                value={value}
                expanded={expanded}
                onExpandedChange={setExpanded}
            >
                <div className={filterMenuTheme.popupContent}>
                    <Scrollable className={filterMenuTheme.popupContentScrollable}>
                        {filters.map(({ filter, active }) => (
                            <FilterToggle
                                key={filter}
                                filter={filter}
                                active={active}
                                onToggle={onToggleFilter}
                            />
                        ))}
                    </Scrollable>
                    <EditorButton onSelect={onClear}>
                        <Loc.Editor.CLEAR_ALL />
                    </EditorButton>
                </div>
            </PopupValueField>
        );

        const tooltipContent = renderTooltip(tooltip);
        return tooltipContent ? <Tooltip tooltip={tooltipContent}>{field}</Tooltip> : field;
    };

    const FilterToggle = ({ filter, active, onToggle }: { filter: string; active: boolean; onToggle: (filter: string, active: boolean) => void }) => {
        const toggle = useCallback(() => onToggle(filter, !active), [active, filter, onToggle]);
        return <ToggleField label={filter} value={active} onChange={toggle} />;
    };

    const BoundFilterMenu = ({ parent, path, props }: BoundFilterMenuProps) => {
        const widgetPath = useMemo(() => resolvePath(parent, path), [parent, path]);
        const onToggleFilter = useCallback(
            (filter: string, active: boolean) => toggleFilter(widgetPath, filter, active),
            [widgetPath],
        );
        const onClear = useCallback(() => clearFilters(widgetPath), [widgetPath]);

        return (
            <FilterMenu
                availableFilters={props.availableFilters}
                activeFilters={props.activeFilters}
                label={props.displayName}
                tooltip={props.tooltip}
                onToggleFilter={onToggleFilter}
                onClear={onClear}
            />
        );
    };

    moduleRegistry.override("game-ui/editor/widgets/filter-menu/filter-menu.tsx", "FilterMenu", FilterMenu);
    moduleRegistry.override("game-ui/editor/widgets/filter-menu/filter-menu.tsx", "BoundFilterMenu", BoundFilterMenu);
    editorWidgetComponents[EditorWidgetType.FilterMenu] = BoundFilterMenu;
};
