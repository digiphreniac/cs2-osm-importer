# cs2-osm-importer

A **Cities: Skylines 2** mod that imports real-world road network data from **[OpenStreetMap](https://wiki.openstreetmap.org/wiki/About_OpenStreetMap) (OSM)** XML files and procedurally generates the corresponding road networks inside the game world.

#### Dependencies
* Unified Icon Library

## Usage
### 1. Dowload OSM Data
OSM data can be downloaded from a variety of sources. \
I recommend you download OSM from [terraining.ateliernonta.com](https://terraining.ateliernonta.com) (more detailed instructions at the [GitHub repo](https://github.com/nonta1234/terraining-heightmap-generator#readme)).

> [!NOTE]
> It is recommended that you also download heightmap data at the website [terraining.ateliernonta.com](https://terraining.ateliernonta.com). \
> <small>
> [Learn more about heightmaps in Cities Skylines II](https://cs2.paradoxwikis.com/Map_Creation#Heightmaps)
> </small>


### 2. Launch editor
This mod was built to work with the in-game editor mode. 

### 3. Open OSM Importer Tool
<!-- TODO - finish this -->
You can open the OSM Importer tool panel by clicking on the button ... ***TODO ! ! ! FINISH ! ! ! TODO***

### 4. Import the OSM Data
Using the OSM Importer tool panel, input the path to the OSM data file from step 1 and import the data. \

> [!NOTE]
> The import process will take some time and likely freeze your game for 10+ seconds.

### 5. Generate roads
Wait patiently while roads generate and enjoy!
Once you've imported the data, you can select different subsets of the roads to generate.

> [!NOTE]
> Be aware the road generation can take some time. \
> Areas with lots of roads will require a lot of compute power.

## How it works (for developers)

The mod follows CS2's **ECS pattern** with a custom `OsmImportDefinition` component and two systems:

- **`OsmImportSystem`** (extends `GameSystemBase`) — handles the full import pipeline via a **state machine** (`WorkPhase`: `Idle` → `LoadData` → `QueueDefinitions` → `WaitForGameApply` → `DeleteRoads`). Road segment generation is **frame-budgeted** (128 segments per frame) to avoid blocking the game loop.
- **`OsmImporterUISystem`** (extends `UISystemBase`) — bridges the in-game UI to the import system.

The UI registers React components into the game UI and communicates with the C# backend through `ValueBinding`/`TriggerBinding` data bindings.

### Version bumping

Use `bump-version.ps1` to update both the mod publish version and `ui/mod.json` in one step:

```powershell
./bump-version.ps1 patch
./bump-version.ps1 minor
./bump-version.ps1 major
```

If you omit the argument, it defaults to `patch`.

## References

- [Cities Skylines II Modding Toolchain](https://cs2.paradoxwikis.com/Modding_Toolchain) - helpful for getting modding development environment
- [CS2 Modding documentation](https://cs2.paradoxwikis.com/Modding)
- This project was based off of the [Cities Skylines OSM Import mod](https://steamcommunity.com/sharedfiles/filedetails/?id=1957515502).
- Related tools/mods:
  - [cs2-minneapolis-osm-toolkit](https://github.com/Osyanne/cs2-minneapolis-osm-toolkit): Real-world GIS data from OpenStreetMap



### Feedback
Report bugs, request features, or provide general feedback by creating an [issue on the Github repo](https://github.com/digiphreniac/cs2-osm-importer/issues). \
Please look at the existing issues before creating one to avoid creating duplicates.
