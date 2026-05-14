import { Icon, Panel } from "cs2/ui";
import { bindValue, bindTriggerWithArgs, useValue } from "cs2/api";
import { useCallback, useEffect, useState } from "react";
import mod from "mod.json";
import styles from "../osm-importer.module.scss";

const MENU_WIDTH = 520;
const MENU_HEIGHT = 760;
const ROAD_TYPES = [
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
];
const DEFAULT_SELECTED_ROAD_TYPES = [...ROAD_TYPES];

type ToolSettings = {
  path: string;
  tolerance: string;
  curveTolerance: string;
  scaleFactor: string;
  maxWays: string;
  selectedRoadTypes: string[];
  generatedRoadTypes: string[];
  generatingRoadTypes: string[];
  importSettingsOpen: boolean;
  roadTypesOpen: boolean;
  lastLoadedImportSettings: ImportSettings | null;
};

type ImportSettings = {
  path: string;
  tolerance: string;
  curveTolerance: string;
  scaleFactor: string;
  maxWays: string;
};

type ImportFieldErrors = {
  tolerance: string | null;
  curveTolerance: string | null;
  scaleFactor: string | null;
  maxWays: string | null;
};

let savedSettings: ToolSettings = {
  path: "",
  tolerance: "1",
  curveTolerance: "0.5",
  scaleFactor: "1",
  maxWays: "10000",
  selectedRoadTypes: DEFAULT_SELECTED_ROAD_TYPES,
  generatedRoadTypes: [],
  generatingRoadTypes: [],
  importSettingsOpen: true,
  roadTypesOpen: false,
  lastLoadedImportSettings: null,
};

const isOSMToolMenuEnabled$ = bindValue<boolean>(mod.id, "isOSMToolMenuEnabled");
const setIsOSMToolMenuEnabled = bindTriggerWithArgs<[boolean]>(mod.id, "setIsOSMToolMenuEnabled");
const importStatus$ = bindValue<string>(mod.id, "importStatus", "Ready.");
const isImporting$ = bindValue<boolean>(mod.id, "isImporting", false);
const defaultOsmPath$ = bindValue<string>(mod.id, "defaultOsmPath", "");
const hasLoadedData$ = bindValue<boolean>(mod.id, "hasLoadedData", false);
const roadTypeCounts$ = bindValue<string>(mod.id, "roadTypeCounts", "{}");
const loadOSMData = bindTriggerWithArgs<[string]>(mod.id, "loadOSMData");
const generateOSMRoads = bindTriggerWithArgs<[string]>(mod.id, "generateOSMRoads");
const deleteAllRoads = bindTriggerWithArgs<[string]>(mod.id, "deleteAllRoads");
const isDebugMode$ = bindValue<boolean>(mod.id, "isDebugMode", false);
const tryValidate = bindTriggerWithArgs<[string]>(mod.id, "tryValidate");
const FOLDER_ICON = "coui://uil/Standard/Folder.svg";
const SHOW_ICON = "coui://uil/Standard/ArrowDownThickStroke.svg";
const HIDE_ICON = "coui://uil/Standard/ArrowUpThickStroke.svg";
const STEP_UP_ICON = "coui://uil/Standard/ArrowUpThickStroke.svg";
const STEP_DOWN_ICON = "coui://uil/Standard/ArrowDownThickStroke.svg";
const GENERATING_ICON = "coui://uil/Standard/ClockFilled.svg";
const GENERATED_ICON = "coui://uil/Standard/Checkmark.svg";
const INFO_ICON = "coui://uil/Standard/CircleInfo.svg";

const DEFAULT_IMPORT_SETTINGS = {
  tolerance: "1",
  curveTolerance: "0.5",
  scaleFactor: "1",
  maxWays: "10000",
} as const;

const DEFAULT_IMPORT_FIELD_ERRORS: ImportFieldErrors = {
  tolerance: null,
  curveTolerance: null,
  scaleFactor: null,
  maxWays: null,
};

export const ModToolMenu = ({
  isOpen,
  onClose,
}: {
  isOpen?: boolean;
  onClose?: () => void;
}) => {
  const boundIsOpen = useValue(isOSMToolMenuEnabled$);
  const isOSMToolMenuEnabled = isOpen ?? boundIsOpen ?? false;
  const importStatus = useValue(importStatus$);
  const isImporting = useValue(isImporting$);
  const defaultOsmPath = useValue(defaultOsmPath$);
  const hasLoadedData = useValue(hasLoadedData$);
  const roadTypeCountsJson = useValue(roadTypeCounts$);
  const isDebugMode = useValue(isDebugMode$);

  const [path, setPath] = useState(savedSettings.path);
  const [tolerance, setTolerance] = useState(savedSettings.tolerance);
  const [curveTolerance, setCurveTolerance] = useState(savedSettings.curveTolerance);
  const [scaleFactor, setScaleFactor] = useState(savedSettings.scaleFactor);
  const [maxWays, setMaxWays] = useState(savedSettings.maxWays);
  const [selectedRoadTypes, setSelectedRoadTypes] = useState<string[]>(
    savedSettings.selectedRoadTypes,
  );
  const [generatedRoadTypes, setGeneratedRoadTypes] = useState<string[]>(
    savedSettings.generatedRoadTypes,
  );
  const [generatingRoadTypes, setGeneratingRoadTypes] = useState<string[]>(
    savedSettings.generatingRoadTypes,
  );
  const [importSettingsOpen, setImportSettingsOpen] = useState(savedSettings.importSettingsOpen);
  const [roadTypesOpen, setRoadTypesOpen] = useState(savedSettings.roadTypesOpen);
  const [lastLoadedImportSettings, setLastLoadedImportSettings] = useState<ImportSettings | null>(
    savedSettings.lastLoadedImportSettings,
  );
  const [pendingLoadSettings, setPendingLoadSettings] = useState<ImportSettings | null>(null);
  const [localStatus, setLocalStatus] = useState<string | null>(null);
  const [currentGenerationRoadType, setCurrentGenerationRoadType] = useState<string | null>(null);
  const [pathError, setPathError] = useState<string | null>(null);
  const [importFieldErrors, setImportFieldErrors] = useState<ImportFieldErrors>(
    DEFAULT_IMPORT_FIELD_ERRORS,
  );
  const [suppressStalePathError, setSuppressStalePathError] = useState(false);
  const [isLoadPending, setIsLoadPending] = useState(false);
  const [generationAwaitingStart, setGenerationAwaitingStart] = useState(false);
  const roadTypeCounts = parseRoadTypeCounts(roadTypeCountsJson);
  const showSectionToggleIcons = hasLoadedData;
  const sortedRoadTypes = [...ROAD_TYPES].sort((left, right) => {
    const countDelta = (roadTypeCounts[right] ?? 0) - (roadTypeCounts[left] ?? 0);
    return countDelta !== 0 ? countDelta : left.localeCompare(right);
  });
  const selectedUngeneratedRoadTypes = selectedRoadTypes.filter(
    (roadType) => !generatedRoadTypes.includes(roadType) && !generatingRoadTypes.includes(roadType),
  );
  const canGenerateRoads = hasLoadedData && !isImporting;
  const activeRoadTypes = sortedRoadTypes.filter(
    (roadType) =>
      (roadTypeCounts[roadType] ?? 0) > 0 &&
      !generatedRoadTypes.includes(roadType) &&
      !generatingRoadTypes.includes(roadType),
  );
  const activeRoadTypeRoadCount = activeRoadTypes.reduce(
    (sum, roadType) => sum + (roadTypeCounts[roadType] ?? 0),
    0,
  );
  const allSelectableRoadTypesSelected =
    activeRoadTypes.length > 0 && activeRoadTypes.every((roadType) => selectedRoadTypes.includes(roadType));
  const isImportSettingsDirty = !lastLoadedImportSettings
    ? true
    : lastLoadedImportSettings.path !== path ||
      lastLoadedImportSettings.tolerance !== tolerance ||
      lastLoadedImportSettings.curveTolerance !== curveTolerance ||
      lastLoadedImportSettings.scaleFactor !== scaleFactor ||
      lastLoadedImportSettings.maxWays !== maxWays;
  const isLoadButtonWorking = isImporting || isLoadPending;
  const hasImportFieldErrors = Object.values(importFieldErrors).some(Boolean);
  const isLoadButtonDisabled = isImporting || hasImportFieldErrors || (hasLoadedData && !isImportSettingsDirty);

  const getImportFieldConfig = (field: "tolerance" | "curveTolerance" | "scaleFactor" | "maxWays") => {
    switch (field) {
      case "tolerance":
      case "curveTolerance":
        return { min: 0, max: 10, step: 0.5, fallback: DEFAULT_IMPORT_SETTINGS[field] };
      case "scaleFactor":
        return { min: 0.1, max: 50, step: 0.1, fallback: DEFAULT_IMPORT_SETTINGS[field] };
      case "maxWays":
        return { min: 1000, max: 100000, step: 1000, fallback: DEFAULT_IMPORT_SETTINGS[field] };
      default:
        return { min: 0, max: 0, step: 1, fallback: "0" };
    }
  };

  const validateImportField = (field: keyof ImportFieldErrors, value: string) => {
    const trimmed = value.trim();
    if (trimmed === "") return "Enter a value.";

    switch (field) {
      case "tolerance":
      case "curveTolerance": {
        const parsed = Number(trimmed);
        if (!Number.isFinite(parsed)) return "Enter a number.";
        if (parsed < 0) return "Use 0 or greater.";
        if (!isMultipleOf(parsed, 0.5)) return "Use 0.5 increments.";
        return null;
      }
      case "scaleFactor": {
        const parsed = Number(trimmed);
        if (!Number.isFinite(parsed)) return "Enter a number.";
        if (parsed <= 0) return "Use a number greater than 0.";
        if (!isMultipleOf(parsed, 0.1)) return "Use 0.1 increments.";
        return null;
      }
      case "maxWays": {
        const parsed = Number(trimmed);
        if (!Number.isFinite(parsed)) return "Enter a whole number.";
        if (!Number.isInteger(parsed)) return "Enter a whole number.";
        if (parsed <= 0) return "Use a positive number.";
        if (parsed % 1000 !== 0) return "Use 1000 increments.";
        return null;
      }
      default:
        return null;
    }
  };

  const updateImportField = (
    field: keyof ImportFieldErrors,
    setter: (value: string) => void,
  ) => (rawValue: string) => {
    const current = field === "tolerance" ? tolerance : field === "curveTolerance" ? curveTolerance : field === "scaleFactor" ? scaleFactor : maxWays;
    const sanitizedValue = sanitizeImportFieldInput(field, rawValue, current);
    setter(sanitizedValue);
    setImportFieldErrors((current) => ({
      ...current,
      [field]: validateImportField(field, sanitizedValue),
    }));
  };

  const validateAllImportFields = () => {
    const nextErrors: ImportFieldErrors = {
      tolerance: validateImportField("tolerance", tolerance),
      curveTolerance: validateImportField("curveTolerance", curveTolerance),
      scaleFactor: validateImportField("scaleFactor", scaleFactor),
      maxWays: validateImportField("maxWays", maxWays),
    };

    setImportFieldErrors(nextErrors);
    return !Object.values(nextErrors).some(Boolean);
  };

  const handleLoad = () => {
    if (!validateAllImportFields()) {
      return;
    }

    setPathError(null);
    setSuppressStalePathError(true);

    setIsLoadPending(true);
    setGenerationAwaitingStart(false);
    setCurrentGenerationRoadType(null);
    setGeneratedRoadTypes([]);
    setGeneratingRoadTypes([]);
    setPendingLoadSettings({
      path,
      tolerance,
      curveTolerance,
      scaleFactor,
      maxWays,
    });
    setLocalStatus("Loading...");
    window.setTimeout(() => {
      loadOSMData(
        JSON.stringify({
          path,
          tolerance: parseNumericInput(tolerance, 1),
          curveTolerance: parseNumericInput(curveTolerance, 0.5),
          scaleFactor: parseNumericInput(scaleFactor, 1),
          maxWays: parseIntegerInput(maxWays, 10000),
        }),
      );
    }, 50);
    window.setTimeout(() => {
      setSuppressStalePathError(false);
      setIsLoadPending(false);
    }, 1000);
  };

  const handleResetImportSettings = () => {
    setPath(defaultOsmPath || "");
    setTolerance(DEFAULT_IMPORT_SETTINGS.tolerance);
    setCurveTolerance(DEFAULT_IMPORT_SETTINGS.curveTolerance);
    setScaleFactor(DEFAULT_IMPORT_SETTINGS.scaleFactor);
    setMaxWays(DEFAULT_IMPORT_SETTINGS.maxWays);
    setPathError(null);
    setImportFieldErrors(DEFAULT_IMPORT_FIELD_ERRORS);
    setSuppressStalePathError(false);
  };

  const handleGenerate = () => {
    setLocalStatus("Generating...");
    setGenerationAwaitingStart(true);
    setCurrentGenerationRoadType(null);
    setGeneratingRoadTypes(selectedUngeneratedRoadTypes);
    window.setTimeout(() => {
      generateOSMRoads(JSON.stringify({ roadTypes: selectedUngeneratedRoadTypes }));
    }, 50);
  };

  const handleDeleteRoads = () => {
    setLocalStatus("Deleting roads...");
    setGenerationAwaitingStart(false);
    setCurrentGenerationRoadType(null);
    setGeneratedRoadTypes([]);
    setGeneratingRoadTypes([]);
    window.setTimeout(() => {
      deleteAllRoads("{}");
    }, 50);
  };

  const toggleRoadType = (roadType: string) => {
    setSelectedRoadTypes((current) =>
      current.includes(roadType)
        ? current.filter((item) => item !== roadType)
        : [...current, roadType],
    );
  };

  const toggleImportSection = () => {
    if (!hasLoadedData) return;

    setImportSettingsOpen((open) => {
      const nextOpen = !open;
      if (nextOpen) setRoadTypesOpen(false);
      return nextOpen;
    });
  };

  const toggleGenerateSection = () => {
    if (!hasLoadedData) return;

    setRoadTypesOpen((open) => {
      const nextOpen = !open;
      if (nextOpen) setImportSettingsOpen(false);
      return nextOpen;
    });
  };

  useEffect(() => {
    if (!path && defaultOsmPath) {
      setPath(defaultOsmPath);
    }
  }, [defaultOsmPath, path]);

  useEffect(() => {
    savedSettings = {
      path,
      tolerance,
      curveTolerance,
      scaleFactor,
      maxWays,
      selectedRoadTypes,
      generatedRoadTypes,
      generatingRoadTypes,
      importSettingsOpen,
      roadTypesOpen,
      lastLoadedImportSettings,
    };
  }, [
    path,
    tolerance,
    curveTolerance,
    scaleFactor,
    maxWays,
    selectedRoadTypes,
    generatedRoadTypes,
    generatingRoadTypes,
    importSettingsOpen,
    roadTypesOpen,
    lastLoadedImportSettings,
  ]);

  useEffect(() => {
    const isPathErrorStatus =
      importStatus.includes("Enter a valid OSM file path") ||
      importStatus.includes("OSM file was not found") ||
      importStatus.includes("Choose an .osm file") ||
      importStatus.includes("Failed to load OSM data");
    const isPathAcceptedStatus =
      importStatus.includes("OSM file path accepted") ||
      importStatus.includes("Parsing OSM file") ||
      importStatus.includes("Loaded ");
    const isSuppressedStaleLoadStatus =
      suppressStalePathError && localStatus === "Loading..." && (isPathErrorStatus || isPathAcceptedStatus);

    if (localStatus && importStatus !== "Ready." && !isSuppressedStaleLoadStatus) {
      setLocalStatus(null);
    }

    if (isPathErrorStatus && !isSuppressedStaleLoadStatus) {
      setIsLoadPending(false);
    }

    if (isPathErrorStatus && !isSuppressedStaleLoadStatus) {
      setPathError("Enter a valid OSM file path.");
      setPendingLoadSettings(null);
    } else if (isPathAcceptedStatus && !isSuppressedStaleLoadStatus) {
      setSuppressStalePathError(false);
      setIsLoadPending(false);
      setPathError(null);
    }
  }, [importStatus, localStatus, suppressStalePathError]);

  useEffect(() => {
    if (hasLoadedData && importStatus.includes("Loaded ") && pendingLoadSettings) {
      setImportSettingsOpen(false);
      setRoadTypesOpen(true);
      setLastLoadedImportSettings(pendingLoadSettings);
      setPendingLoadSettings(null);
    }
  }, [hasLoadedData, importStatus, pendingLoadSettings]);

  useEffect(() => {
    if (!hasLoadedData) return;

    setImportFieldErrors(DEFAULT_IMPORT_FIELD_ERRORS);
  }, [hasLoadedData]);

  useEffect(() => {
    if (generatingRoadTypes.length === 0 || !importStatus) return;
    const statusGenerationType = parseGenerationTypeFromStatus(importStatus);

    if (generationAwaitingStart) {
      const isGenerationStartStatus =
        importStatus.startsWith("Preparing road generation") ||
        importStatus.startsWith("Generating selected OSM roads") ||
        importStatus.startsWith("Queued ") ||
        importStatus.startsWith("Generating \"");

      if (!isGenerationStartStatus) return;

      setGenerationAwaitingStart(false);
      setCurrentGenerationRoadType(statusGenerationType ?? generatingRoadTypes[0] ?? null);
      return;
    }

    if (statusGenerationType && statusGenerationType !== currentGenerationRoadType) {
      if (currentGenerationRoadType) {
        setGeneratedRoadTypes((current) => [...new Set([...current, currentGenerationRoadType])]);
        setGeneratingRoadTypes((current) =>
          current.filter((roadType) => roadType !== currentGenerationRoadType),
        );
      }
      setCurrentGenerationRoadType(statusGenerationType);
    }

    if (isImporting) return;

    if (importStatus.startsWith("Failed")) {
      setSelectedRoadTypes((current) => [...new Set([...current, ...generatingRoadTypes])]);
      setGeneratingRoadTypes([]);
      setCurrentGenerationRoadType(null);
      return;
    }

    if (importStatus.startsWith("CS2 consumed") || importStatus.startsWith("Queued ")) {
      if (currentGenerationRoadType) {
        setGeneratedRoadTypes((current) => [...new Set([...current, currentGenerationRoadType])]);
      }
      setGeneratedRoadTypes((current) => [...new Set([...current, ...generatingRoadTypes])]);
      setGeneratingRoadTypes([]);
      setCurrentGenerationRoadType(null);
    }
  }, [currentGenerationRoadType, generatingRoadTypes, generationAwaitingStart, importStatus, isImporting]);

  useEffect(() => {
    if (!hasLoadedData) return;

    setSelectedRoadTypes((current) =>
      current.filter(
        (roadType) => (roadTypeCounts[roadType] ?? 0) > 0 && !generatedRoadTypes.includes(roadType),
      ),
    );
  }, [hasLoadedData, roadTypeCountsJson, generatedRoadTypes]);

  const handleClose = useCallback(() => {
    if (onClose) {
      onClose();
      return;
    }

    if (isOpen === undefined) {
      setIsOSMToolMenuEnabled(false);
    }
  }, [isOpen, onClose]);

  const adjustImportNumber = (field: "tolerance" | "curveTolerance" | "scaleFactor" | "maxWays", direction: 1 | -1) => {
    const config = {
      tolerance: { value: tolerance, setter: setTolerance },
      curveTolerance: { value: curveTolerance, setter: setCurveTolerance },
      scaleFactor: { value: scaleFactor, setter: setScaleFactor },
      maxWays: { value: maxWays, setter: setMaxWays },
    }[field];
    const fieldConfig = getImportFieldConfig(field);

    const parsed = Number(config.value);
    const base = Number.isFinite(parsed) ? parsed : Number(fieldConfig.fallback);
    const next = clampToStep(base + fieldConfig.step * direction, fieldConfig.min, fieldConfig.max, fieldConfig.step);
    const formatted = formatImportFieldValue(field, next);
    config.setter(formatted);
    setImportFieldErrors((current) => ({
      ...current,
      [field]: validateImportField(field, formatted),
    }));
  };

  if (!isOSMToolMenuEnabled) return <></>;

  return (
    <Panel
      draggable={true}
      header="OSM Import Tool"
      onClose={handleClose}
      style={{
        width: MENU_WIDTH,
        minHeight: MENU_HEIGHT,
        maxHeight: MENU_HEIGHT,
        padding: 0,
        background: "rgba(12, 20, 34, 0.96)",
      }}
    >
      <div style={panelContentStyle}>
          <div style={sectionBlockStyle}>
            <button
              type="button"
              onClick={toggleImportSection}
              style={sectionHeaderStyle(false, !hasLoadedData)}
            >
              <span>1. Import data</span>
              {showSectionToggleIcons && (
                <span style={sectionToggleIconStyle}>
                  <Icon
                    src={importSettingsOpen ? HIDE_ICON : SHOW_ICON}
                    tinted={false}
                    className={styles.sectionIcon}
                  />
                </span>
              )}
            </button>

            {importSettingsOpen && (
              <>
                <div style={sectionSummaryStyle}>
                  Import an OSM file and configure how the data is parsed before it is loaded.
                </div>
                <div style={importGridStyle}>
                  <div style={pathFieldStyle}>
                    <label
                      style={{
                        display: "flex",
                        alignItems: "center",
                        marginBottom: 4,
                        fontSize: 12,
                        color: "#ccc",
                      }}
                    >
                      <span style={fieldIconStyle}>
                        <Icon
                          src={FOLDER_ICON}
                          tinted={false}
                          className={styles.fieldIcon}
                        />
                      </span>
                      OSM file path
                    </label>
                    <input
                      type="text"
                      value={path}
                      onChange={(e) => {
                        setPath(e.target.value);
                        if (pathError) setPathError(null);
                      }}
                      onPaste={(e) => {
                        const text = e.clipboardData.getData("text");
                        if (text) {
                          setPath(text);
                          if (pathError) setPathError(null);
                        }
                      }}
                      onInput={(e) => {
                        setPath((e.target as HTMLInputElement).value);
                        if (pathError) setPathError(null);
                      }}
                      style={inputStyle(Boolean(pathError))}
                      placeholder={defaultOsmPath || "C:\\Users\\<user>\\Downloads\\"}
                    />
                    {pathError && <div style={fieldErrorStyle}>{pathError}</div>}
                  </div>

                  <div style={importFieldRowStyle}>
                    <div style={{ ...fieldGroupStyle, ...importHalfFieldStyle, marginRight: 12 }}>
                      <label style={labelStyle}>Simplify tolerance (m)</label>
                      <div style={numberInputRowStyle}>
                        <input
                          type="number"
                          value={tolerance}
                          onChange={(e) => updateImportField("tolerance", setTolerance)(e.target.value)}
                          min="0"
                          max="10"
                          step="0.5"
                          inputMode="decimal"
                          style={{ ...inputStyle(Boolean(importFieldErrors.tolerance)), ...numberInputStyle }}
                          placeholder={DEFAULT_IMPORT_SETTINGS.tolerance}
                        />
                        <div style={spinnerColumnStyle}>
                          <button type="button" onClick={() => adjustImportNumber("tolerance", 1)} style={spinnerButtonStyle}>
                            <Icon src={STEP_UP_ICON} tinted={false} className={styles.spinnerIcon} />
                          </button>
                          <button type="button" onClick={() => adjustImportNumber("tolerance", -1)} style={spinnerButtonStyle}>
                            <Icon src={STEP_DOWN_ICON} tinted={false} className={styles.spinnerIcon} />
                          </button>
                        </div>
                      </div>
                      {importFieldErrors.tolerance && <div style={fieldErrorStyle}>{importFieldErrors.tolerance}</div>}
                    </div>

                    <div style={{ ...fieldGroupStyle, ...importHalfFieldStyle }}>
                      <label style={labelStyle}>Curve tolerance (m)</label>
                      <div style={numberInputRowStyle}>
                        <input
                          type="number"
                          value={curveTolerance}
                          onChange={(e) => updateImportField("curveTolerance", setCurveTolerance)(e.target.value)}
                          min="0"
                          max="10"
                          step="0.5"
                          inputMode="decimal"
                          style={{ ...inputStyle(Boolean(importFieldErrors.curveTolerance)), ...numberInputStyle }}
                          placeholder={DEFAULT_IMPORT_SETTINGS.curveTolerance}
                        />
                        <div style={spinnerColumnStyle}>
                          <button type="button" onClick={() => adjustImportNumber("curveTolerance", 1)} style={spinnerButtonStyle}>
                            <Icon src={STEP_UP_ICON} tinted={false} className={styles.spinnerIcon} />
                          </button>
                          <button type="button" onClick={() => adjustImportNumber("curveTolerance", -1)} style={spinnerButtonStyle}>
                            <Icon src={STEP_DOWN_ICON} tinted={false} className={styles.spinnerIcon} />
                          </button>
                        </div>
                      </div>
                      {importFieldErrors.curveTolerance && <div style={fieldErrorStyle}>{importFieldErrors.curveTolerance}</div>}
                    </div>

                    <div style={{ ...fieldGroupStyle, ...importHalfFieldStyle, marginRight: 12 }}>
                      <label style={labelStyle}>Scale factor</label>
                      <div style={numberInputRowStyle}>
                        <input
                          type="number"
                          value={scaleFactor}
                          onChange={(e) => updateImportField("scaleFactor", setScaleFactor)(e.target.value)}
                          min="0.1"
                          max="50"
                          step="0.1"
                          inputMode="decimal"
                          style={{ ...inputStyle(Boolean(importFieldErrors.scaleFactor)), ...numberInputStyle }}
                          placeholder={DEFAULT_IMPORT_SETTINGS.scaleFactor}
                        />
                        <div style={spinnerColumnStyle}>
                          <button type="button" onClick={() => adjustImportNumber("scaleFactor", 1)} style={spinnerButtonStyle}>
                            <Icon src={STEP_UP_ICON} tinted={false} className={styles.spinnerIcon} />
                          </button>
                          <button type="button" onClick={() => adjustImportNumber("scaleFactor", -1)} style={spinnerButtonStyle}>
                            <Icon src={STEP_DOWN_ICON} tinted={false} className={styles.spinnerIcon} />
                          </button>
                        </div>
                      </div>
                      {importFieldErrors.scaleFactor && <div style={fieldErrorStyle}>{importFieldErrors.scaleFactor}</div>}
                    </div>

                    <div style={{ ...fieldGroupStyle, ...importHalfFieldStyle }}>
                      <label style={labelStyle}>Max ways</label>
                      <div style={numberInputRowStyle}>
                        <input
                          type="number"
                          value={maxWays}
                          onChange={(e) => updateImportField("maxWays", setMaxWays)(e.target.value)}
                          min="1"
                          max="100000"
                          step="1000"
                          inputMode="numeric"
                          style={{ ...inputStyle(Boolean(importFieldErrors.maxWays)), ...numberInputStyle }}
                          placeholder={DEFAULT_IMPORT_SETTINGS.maxWays}
                        />
                        <div style={spinnerColumnStyle}>
                          <button type="button" onClick={() => adjustImportNumber("maxWays", 1)} style={spinnerButtonStyle}>
                            <Icon src={STEP_UP_ICON} tinted={false} className={styles.spinnerIcon} />
                          </button>
                          <button type="button" onClick={() => adjustImportNumber("maxWays", -1)} style={spinnerButtonStyle}>
                            <Icon src={STEP_DOWN_ICON} tinted={false} className={styles.spinnerIcon} />
                          </button>
                        </div>
                      </div>
                      {importFieldErrors.maxWays && <div style={fieldErrorStyle}>{importFieldErrors.maxWays}</div>}
                    </div>
                  </div>

                  <div style={importActionRowStyle}>
                    <button
                      onClick={handleResetImportSettings}
                      disabled={isLoadButtonWorking}
                      style={{
                        ...buttonStyle(
                          isLoadButtonWorking,
                          "rgba(88, 98, 112, 0.88)",
                          "rgba(124, 137, 155, 1)",
                        ),
                        width: "49%",
                        alignSelf: "auto",
                        marginRight: "2%",
                      }}
                    >
                      Reset to default
                    </button>
                    <button
                      onClick={handleLoad}
                      disabled={isLoadButtonWorking || isLoadButtonDisabled}
                      style={{
                        ...buttonStyle(
                          isLoadButtonWorking || isLoadButtonDisabled,
                          "rgba(66, 135, 245, 0.85)",
                          "rgba(66, 135, 245, 1)",
                        ),
                        width: "49%",
                        alignSelf: "auto",
                      }}
                    >
                      {isLoadButtonWorking ? "Working..." : "Load OSM Data"}
                    </button>
                  </div>
                </div>
              </>
            )}
          </div>

          <div style={sectionBlockStyle}>
            <button
              type="button"
              onClick={toggleGenerateSection}
              disabled={!hasLoadedData}
              style={sectionHeaderStyle(!hasLoadedData)}
            >
              <span>2. Generate roads</span>
              {showSectionToggleIcons && (
                <span style={sectionToggleIconStyle}>
                  <Icon
                    src={roadTypesOpen ? HIDE_ICON : SHOW_ICON}
                    tinted={false}
                    className={styles.sectionIcon}
                  />
                </span>
              )}
            </button>

            {hasLoadedData && roadTypesOpen && (
              <>
                <div style={sectionSummaryStyle}>
                  Pick which loaded road types should be generated in game, or clear existing roads to start over.
                </div>
                <div style={roadTypeGridStyle}>
                  {sortedRoadTypes.map((roadType) => {
                    const isGenerated = generatedRoadTypes.includes(roadType);
                    const isGenerating = generatingRoadTypes.includes(roadType);
                    const count = roadTypeCounts[roadType] ?? 0;
                    const isDisabled = !canGenerateRoads || isGenerated || isGenerating || count === 0;
                    const isSelected = selectedRoadTypes.includes(roadType) && !isDisabled;
                    return (
                      <button
                        key={roadType}
                        type="button"
                        onClick={() => toggleRoadType(roadType)}
                        disabled={isDisabled}
                        style={roadTypeToggleStyle(isSelected, isDisabled)}
                      >
                        <span style={roadTypeNameStyle}>{roadType}</span>
                        {(isGenerating || isGenerated) && (
                          <span style={roadTypeStatusIconStyle}>
                            <Icon
                              src={isGenerating ? GENERATING_ICON : GENERATED_ICON}
                              tinted={false}
                              className={styles.roadStatusIcon}
                            />
                          </span>
                        )}
                        <span style={roadTypeCountStyle}>{hasLoadedData ? count : "-"}</span>
                      </button>
                    );
                  })}
                  <button
                    type="button"
                    onClick={() => {
                      if (allSelectableRoadTypesSelected) {
                        setSelectedRoadTypes((current) =>
                          current.filter((roadType) => !activeRoadTypes.includes(roadType)),
                        );
                      } else {
                        setSelectedRoadTypes((current) => [
                          ...new Set([...current, ...activeRoadTypes]),
                        ]);
                      }
                    }}
                    disabled={!canGenerateRoads || activeRoadTypes.length === 0}
                    style={selectAllToggleStyle(
                      allSelectableRoadTypesSelected,
                      !canGenerateRoads || activeRoadTypes.length === 0,
                    )}
                  >
                    <span style={roadTypeNameStyle}>
                      {allSelectableRoadTypesSelected ? "Deselect all" : "Select all"}
                    </span>
                    <span style={roadTypeCountStyle}>{activeRoadTypeRoadCount}</span>
                  </button>
                </div>
              </>
            )}

            {!hasLoadedData && <div style={disabledHintStyle}>Load OSM data before generating roads.</div>}
            {hasLoadedData && roadTypesOpen && (
              <div style={actionRowStyle}>
                <button
                  onClick={handleDeleteRoads}
                  disabled={isImporting || generatedRoadTypes.length === 0}
                  style={{
                    ...buttonStyle(
                      isImporting || generatedRoadTypes.length === 0,
                      "rgba(190, 70, 70, 0.85)",
                      "rgba(220, 80, 80, 1)",
                    ),
                    width: "49%",
                    alignSelf: "auto",
                    marginRight: "2%",
                  }}
                >
                  Delete All Roads
                </button>
                <button
                  onClick={handleGenerate}
                  disabled={isImporting || selectedUngeneratedRoadTypes.length === 0}
                  style={{
                    ...buttonStyle(
                      isImporting || selectedUngeneratedRoadTypes.length === 0,
                      "rgba(76, 175, 80, 1)",
                      "rgba(76, 175, 80, 1)",
                    ),
                    width: "49%",
                    alignSelf: "auto",
                  }}
                >
                  Generate Roads
                </button>
              </div>
            )}
          </div>

        {isDebugMode && (
          <div style={{ marginTop: 2, marginBottom: 4, width: "100%" }}>
            <button
              onClick={() => tryValidate(JSON.stringify({}))}
              disabled={false}
              style={{
                ...buttonStyle(false, "rgba(100,100,120,0.7)", "rgba(140,140,160,1)"),
                width: "100%",
              }}
            >
              Debug: Validate road names
            </button>
          </div>
        )}

        <div style={consoleStatusStyle}>
          <span style={consoleIconStyle}>
            <Icon src={INFO_ICON} tinted={false} className={styles.consoleIcon} />
          </span>
          <span style={consoleTextStyle}>{localStatus ?? importStatus}</span>
        </div>
      </div>
    </Panel>
  );
};

const labelStyle = {
  display: "block",
  marginBottom: 6,
  fontSize: 12,
  color: "#ccc",
};

const fieldGroupStyle = {
  display: "flex",
  flexDirection: "column" as const,
};

const numberInputRowStyle = {
  display: "flex",
  alignItems: "stretch",
};

const numberInputStyle = {
  flex: "1 1 auto",
  minWidth: 0,
};

const spinnerColumnStyle = {
  display: "flex",
  flexDirection: "column" as const,
  width: 22,
  flex: "0 0 auto",
  marginLeft: 6,
};

const spinnerButtonStyle = {
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
  width: 22,
  height: 18,
  padding: 0,
  margin: 0,
  backgroundColor: "rgba(30, 40, 55, 0.8)",
  border: "1px solid rgba(255,255,255,0.12)",
  color: "#d8e0ea",
  cursor: "pointer",
  boxSizing: "border-box" as const,
};

const panelContentStyle = {
  padding: 18,
  display: "flex",
  flexDirection: "column" as const,
  maxHeight: MENU_HEIGHT - 58,
  overflowY: "auto" as const,
  boxSizing: "border-box" as const,
};

const sectionBlockStyle = {
  display: "flex",
  flexDirection: "column" as const,
  marginBottom: 28,
};

const sectionHeaderStyle = (disabled: boolean, locked = false) => ({
  display: "flex",
  alignItems: "center",
  justifyContent: "space-between",
  width: "100%",
  padding: "7px 10px",
  backgroundColor: disabled ? "rgba(30, 40, 55, 0.38)" : "rgba(30, 40, 55, 0.72)",
  color: disabled ? "rgba(216, 224, 234, 0.48)" : "#d8e0ea",
  border: "1px solid rgba(255,255,255,0.12)",
  borderRadius: 4,
  fontSize: 12,
  fontWeight: 600,
  cursor: disabled || locked ? "default" : "pointer",
  boxSizing: "border-box" as const,
});

const importGridStyle = {
  display: "flex",
  flexDirection: "column" as const,
  marginTop: 12,
};

const importActionRowStyle = {
  display: "flex",
  width: "100%",
  alignItems: "center",
  marginTop: 10,
};

const sectionSummaryStyle = {
  width: "100%",
  marginBottom: 2,
  padding: "8px 10px",
  backgroundColor: "rgba(18, 28, 42, 0.6)",
  border: "1px solid rgba(255,255,255,0.08)",
  borderRadius: 4,
  color: "rgba(216, 224, 234, 0.82)",
  fontSize: 12,
  lineHeight: 1.35,
  boxSizing: "border-box" as const,
};

const pathFieldStyle = {
  ...fieldGroupStyle,
  marginBottom: 12,
};

const importFieldRowStyle = {
  display: "flex",
  flexWrap: "wrap" as const,
  marginTop: -12,
};

const importHalfFieldStyle = {
  width: "48%",
  marginTop: 12,
};

const fieldErrorStyle = {
  marginTop: 6,
  color: "rgba(255, 125, 125, 1)",
  fontSize: 12,
  lineHeight: 1.25,
};

const disabledHintStyle = {
  marginTop: 12,
  padding: "8px 10px",
  backgroundColor: "rgba(30, 40, 55, 0.36)",
  color: "rgba(216, 224, 234, 0.62)",
  border: "1px solid rgba(255,255,255,0.08)",
  borderRadius: 4,
  fontSize: 12,
};

const consoleStatusStyle = {
  display: "flex",
  alignItems: "center",
  marginTop: 4,
  minHeight: 44,
  padding: "10px 12px",
  backgroundColor: "rgba(5, 9, 15, 0.92)",
  color: "#d8e0ea",
  border: "1px solid rgba(255,255,255,0.12)",
  borderRadius: 4,
  fontSize: 12,
  lineHeight: 1.35,
  boxSizing: "border-box" as const,
};

const consoleIconStyle = {
  display: "inline-flex",
  alignItems: "center",
  justifyContent: "center",
  width: 16,
  height: 16,
  flex: "0 0 auto",
  marginRight: 10,
};

const consoleTextStyle = {
  flex: "1 1 auto",
  fontFamily: "monospace",
  whiteSpace: "pre-wrap" as const,
};

const actionRowStyle = {
  display: "flex",
  width: "100%",
  alignItems: "center",
  marginTop: 4,
  marginBottom: 28,
};

const inputStyle = (hasError: boolean) => ({
  width: "100%",
  padding: "8px 10px",
  backgroundColor: "rgba(30, 40, 55, 0.8)",
  color: "#fff",
  border: `1px solid ${hasError ? "rgba(255, 90, 90, 1)" : "rgba(255,255,255,0.12)"}`,
  borderRadius: 4,
  fontSize: 13,
  boxSizing: "border-box" as const,
});

const parseNumericInput = (value: string, fallback: number) => {
  if (value.trim() === "") return fallback;

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
};

const parseIntegerInput = (value: string, fallback: number) => {
  if (value.trim() === "") return fallback;

  const parsed = parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : fallback;
};

const isMultipleOf = (value: number, step: number) => {
  if (!Number.isFinite(value) || !Number.isFinite(step) || step === 0) return false;

  const quotient = value / step;
  return Math.abs(Math.round(quotient) - quotient) < 1e-9;
};

const parseGenerationTypeFromStatus = (status: string) => {
  if (!status.startsWith("Generating \"")) return null;

  const firstQuote = status.indexOf('"');
  const secondQuote = status.indexOf('"', firstQuote + 1);
  if (firstQuote < 0 || secondQuote < 0) return null;

  return status.slice(firstQuote + 1, secondQuote);
};

const clampToStep = (value: number, min: number, max: number, step: number) => {
  const clamped = Math.min(max, Math.max(min, value));
  const snapped = Math.round(clamped / step) * step;
  return Math.min(max, Math.max(min, snapped));
};

const formatImportFieldValue = (field: "tolerance" | "curveTolerance" | "scaleFactor" | "maxWays", value: number) => {
  switch (field) {
    case "tolerance":
    case "curveTolerance":
      return (Math.round(value * 10) / 10).toString();
    case "scaleFactor":
      return (Math.round(value * 10) / 10).toString();
    case "maxWays":
      return Math.round(value).toString();
    default:
      return value.toString();
  }
};

const sanitizeImportFieldInput = (
  field: "tolerance" | "curveTolerance" | "scaleFactor" | "maxWays",
  rawValue: string,
  currentValue: string,
) => {
  const config = {
    tolerance: { min: 0, max: 10, step: 0.5, fallback: DEFAULT_IMPORT_SETTINGS.tolerance, integerOnly: false },
    curveTolerance: { min: 0, max: 10, step: 0.5, fallback: DEFAULT_IMPORT_SETTINGS.curveTolerance, integerOnly: false },
    scaleFactor: { min: 0.1, max: 50, step: 0.1, fallback: DEFAULT_IMPORT_SETTINGS.scaleFactor, integerOnly: false },
    maxWays: { min: 1000, max: 100000, step: 1000, fallback: DEFAULT_IMPORT_SETTINGS.maxWays, integerOnly: true },
  }[field];

  const trimmed = rawValue.trim();
  if (trimmed === "") {
    return config.fallback;
  }

  const allowedPattern = config.integerOnly ? /^\d+$/ : /^\d*(?:\.\d*)?$/;
  if (!allowedPattern.test(trimmed)) {
    return currentValue;
  }

  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed)) {
    return currentValue;
  }

  const snapped = clampToStep(parsed, config.min, config.max, config.step);
  return formatImportFieldValue(field, snapped);
};

const roadTypeGridStyle = {
  display: "flex",
  flexWrap: "wrap" as const,
  alignItems: "stretch",
  overflowY: "visible" as const,
  position: "relative" as const,
  zIndex: 1,
  padding: 10,
  marginTop: 12,
  backgroundColor: "rgba(30, 40, 55, 0.55)",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: 4,
};

const roadTypeToggleStyle = (selected: boolean, disabled: boolean) => ({
  display: "flex",
  alignItems: "center",
  width: "49%",
  marginRight: "1%",
  marginBottom: 6,
  minHeight: 22,
  padding: "4px 6px",
  position: "relative" as const,
  zIndex: 1,
  backgroundColor: selected ? "rgba(64, 130, 90, 0.55)" : "rgba(18, 28, 42, 0.7)",
  border: `1px solid ${selected ? "rgba(104, 190, 130, 0.9)" : "rgba(255,255,255,0.12)"}`,
  borderRadius: 4,
  fontSize: 11,
  color: disabled ? "rgba(216, 224, 234, 0.55)" : "#d8e0ea",
  cursor: disabled ? "default" : "pointer",
});

const roadTypeNameStyle = {
  flex: "1 1 auto",
  overflow: "hidden",
  textOverflow: "ellipsis",
  marginRight: 7,
};

const roadTypeCountStyle = {
  flex: "0 0 auto",
  minWidth: 30,
  textAlign: "right" as const,
  color: "rgba(225, 232, 240, 0.85)",
  marginRight: 7,
};

const sectionToggleIconStyle = {
  display: "inline-flex",
  alignItems: "center",
  justifyContent: "center",
  width: 12,
  height: 12,
  flex: "0 0 auto",
  marginLeft: 8,
};

const roadTypeStatusIconStyle = {
  display: "inline-flex",
  alignItems: "center",
  justifyContent: "center",
  width: 10,
  height: 10,
  flex: "0 0 auto",
  marginRight: 7,
};

const fieldIconStyle = {
  display: "inline-flex",
  alignItems: "center",
  justifyContent: "center",
  marginRight: 6,
};

const selectAllToggleStyle = (selected: boolean, disabled: boolean) => ({
  display: "flex",
  alignItems: "center",
  width: "49%",
  marginRight: "1%",
  marginBottom: 6,
  minHeight: 22,
  padding: "4px 6px",
  position: "relative" as const,
  zIndex: 1,
  backgroundColor: disabled
    ? "rgba(20, 24, 32, 0.82)"
    : selected
      ? "rgba(94, 74, 34, 0.88)"
      : "rgba(56, 44, 24, 0.82)",
  border: `1px solid ${
    disabled
      ? "rgba(255,255,255,0.09)"
      : selected
        ? "rgba(230, 180, 90, 0.9)"
        : "rgba(210, 160, 72, 0.55)"
  }`,
  borderRadius: 4,
  fontSize: 11,
  color: disabled ? "rgba(216, 224, 234, 0.48)" : "#f3e7ca",
  cursor: disabled ? "default" : "pointer",
  boxShadow: disabled ? "none" : "inset 0 0 0 1px rgba(255,255,255,0.03)",
});

const buttonStyle = (disabled: boolean, color: string, borderColor: string) => ({
  width: "50%",
  alignSelf: "flex-end",
  marginTop: 4,
  marginBottom: 4,
  padding: "8px 16px",
  backgroundColor: disabled ? "rgba(70, 80, 95, 0.8)" : color,
  color: "#fff",
  border: `1px solid ${disabled ? "rgba(90, 100, 115, 1)" : borderColor}`,
  borderRadius: 4,
  fontSize: 13,
  fontWeight: 600,
  cursor: disabled ? "default" : "pointer",
});

const parseRoadTypeCounts = (json?: string): Record<string, number> => {
  try {
    const parsed = JSON.parse(json || "{}");
    return typeof parsed === "object" && parsed ? parsed : {};
  } catch {
    return {};
  }
};
