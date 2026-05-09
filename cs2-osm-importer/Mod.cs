using Colossal.IO.AssetDatabase;
using Colossal.Logging;

using Game;
using Game.Input;
using Game.Modding;
using Game.SceneFlow;

// using Game.Debug;
// using System;
// using Game.UI;

using Unity.Entities;
using UnityEngine.InputSystem;


namespace cs2_osm_importer
{
    public class Mod : IMod
    {
        public const string Id = "cs2-osm-importer";

        public static ILog log = LogManager.GetLogger($"{nameof(cs2_osm_importer)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(true);

        public const string ButtonActionName = "ToggleOSMImporterTool";
        
        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }
    }
}