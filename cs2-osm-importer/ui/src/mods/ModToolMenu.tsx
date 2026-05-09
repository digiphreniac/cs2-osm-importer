import { Panel } from "cs2/ui";
import { call } from "cs2/api"; // https://discord.com/channels/1024242828114673724/1222219069021884527/1406259112714436619 (only explanation)
import { trigger, bindValue, bindTriggerWithArgs, useValue } from "cs2/api";
import { useCallback, useState } from "react";
import mod from "mod.json";

const MENU_WIDTH = 340;
const MENU_HEIGHT = 380;

const isOSMToolMenuEnabled$ = bindValue<boolean>(mod.id, "isOSMToolMenuEnabled");
const setIsOSMToolMenuEnabled = bindTriggerWithArgs<[boolean]>(mod.id, "setIsOSMToolMenuEnabled");


export const ModToolMenu = ({ onClose }: { onClose?: () => void }) => {
  const isOSMToolMenuEnabled = useValue(isOSMToolMenuEnabled$);

  const [path, setPath] = useState("");
  const [tolerance, setTolerance] = useState(0.5);
  const [curveTolerance, setCurveTolerance] = useState(0.5);
  const [scaleFactor, setScaleFactor] = useState(1);

  const handleImport = () => {
    console.log("Import clicked with:", {
      path,
      tolerance,
      curveTolerance,
      scaleFactor,
    });
//     call('ImportOSMData', {
//     path,
//     tolerance,
//     curveTolerance,
//     scaleFactor,
//   });
  };

  const handleClose = useCallback(() => {
    setIsOSMToolMenuEnabled(false);
  }, []);

  if (!isOSMToolMenuEnabled$) return <></>;

  return (
    <Panel
      draggable={true}
      header="OSM Import Tool"
      onClose={onClose}
      style={{
        width: MENU_WIDTH,
        minHeight: MENU_HEIGHT,
        padding: 0,
        background: "rgba(12, 20, 34, 0.96)",
      }}
    >
      <div style={{ padding: 16, display: "flex", flexDirection: "column", gap: 12 }}>
        <div>
          <label style={{ display: "block", marginBottom: 4, fontSize: 12, color: "#ccc" }}>
            Path
          </label>
          <input
            type="text"
            value={path}
            onChange={(e) => setPath(e.target.value)}
            style={{
              width: "100%",
              padding: "6px 8px",
              backgroundColor: "rgba(30, 40, 55, 0.8)",
              color: "#fff",
              border: "1px solid rgba(255,255,255,0.12)",
              borderRadius: 4,
              fontSize: 13,
              boxSizing: "border-box",
            }}
            placeholder="File path..."
          />
        </div>

        <div>
          <label style={{ display: "block", marginBottom: 4, fontSize: 12, color: "#ccc" }}>
            Tolerance
          </label>
          <input
            type="number"
            value={tolerance}
            onChange={(e) => setTolerance(parseFloat(e.target.value))}
            step="0.1"
            style={{
              width: "100%",
              padding: "6px 8px",
              backgroundColor: "rgba(30, 40, 55, 0.8)",
              color: "#fff",
              border: "1px solid rgba(255,255,255,0.12)",
              borderRadius: 4,
              fontSize: 13,
              boxSizing: "border-box",
            }}
          />
        </div>

        <div>
          <label style={{ display: "block", marginBottom: 4, fontSize: 12, color: "#ccc" }}>
            Curve Tolerance
          </label>
          <input
            type="number"
            value={curveTolerance}
            onChange={(e) => setCurveTolerance(parseFloat(e.target.value))}
            step="0.1"
            style={{
              width: "100%",
              padding: "6px 8px",
              backgroundColor: "rgba(30, 40, 55, 0.8)",
              color: "#fff",
              border: "1px solid rgba(255,255,255,0.12)",
              borderRadius: 4,
              fontSize: 13,
              boxSizing: "border-box",
            }}
          />
        </div>

        <div>
          <label style={{ display: "block", marginBottom: 4, fontSize: 12, color: "#ccc" }}>
            Scale Factor
          </label>
          <input
            type="number"
            value={scaleFactor}
            onChange={(e) => setScaleFactor(parseInt(e.target.value, 10))}
            step="1"
            style={{
              width: "100%",
              padding: "6px 8px",
              backgroundColor: "rgba(30, 40, 55, 0.8)",
              color: "#fff",
              border: "1px solid rgba(255,255,255,0.12)",
              borderRadius: 4,
              fontSize: 13,
              boxSizing: "border-box",
            }}
          />
        </div>

        <button
          onClick={handleImport}
          style={{
            padding: "8px 16px",
            backgroundColor: "rgba(76, 175, 80, 0.8)",
            color: "#fff",
            border: "1px solid rgba(76, 175, 80, 1)",
            borderRadius: 4,
            fontSize: 13,
            fontWeight: 600,
            cursor: "pointer",
            marginTop: 8,
          }}
        >
          Import
        </button>
      </div>
    </Panel>
  );
}
