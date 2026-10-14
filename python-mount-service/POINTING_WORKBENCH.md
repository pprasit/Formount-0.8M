# Pointing model web workbench

Calibration now opens with centered **New Model** and **Review Existing Model** choices. New Model fades into a location step with editable latitude, longitude and elevation, plus a clickable map and Back/Next controls. Next saves a model-location draft without changing the observer location used by the running mount service. Existing model review inspects a file without activating it; activation remains an explicit candidate-review action. The previous calibration controls are retained but hidden pending the next workflow step.

The map uses the same Leaflet 1.9.4 and Esri World Imagery / World Topo Map layers as https://operations.narit.or.th/, with imagery selected initially and a Satellite/Map switch. Set `NARIT_POINTING_MAP_PROVIDER=osm` to select OpenStreetMap instead. Leaflet assets are served locally; tiles require network access. `NARIT_GOOGLE_MAPS_API_KEY` optionally selects the Google Maps implementation. Manual coordinate entry remains available if map loading fails.

The C# controls and managers are the source for these operations:

| C# source | Web control |
| --- | --- |
| `UI/MountCalibrationControl.xaml` and `.cs` | New/load model, backend selection, camera control, grid settings, current-position capture, sky/image views, point selection and inclusion, start/pause/resume/stop/rerun, progress and measurement table |
| `Engine/Manager/MountCalibrationManager.cs` | Slew → fixed target tracking → RMS settling → native camera capture → PWLib plate solve → measured encoder/J2000 calibration point |
| `Engine/PointingModel/PointingModelGridGenerator.cs` | Azimuth/altitude grid; preserve solved positions when regenerating nearby points |
| `UI/CalibrationCompleteDialog.xaml` | Review fitted metrics, Use This Model, Keep Current Model, download latest build |
| `UI/PointingAndTrackingControl.xaml` | Seeing/tracking frame series, pointing stability across positions, measured FWHM, centroid RMS/jumps/drift, graphs and JSON export |
| `Engine/PointingModel/ExperimentalPointingModelMetrics.cs` | Native PointXP residuals, RMS/max error and fitted coefficient inspection |

Legacy `.pxp reload` serializes solved observations with the vendor's native writer, reloads that file and calibrates. Experimental CoreDirect calibrates the same observations directly in Core. Both export native Cal Star blocks and the actual fitted Model Terms. A single PointXP instance handles runtime corrections. A completed candidate does not replace the active model until **Use This Model** is pressed.

Capture settings follow the C# defaults: 3 s, binning 2, unbinned scale 0.6 arcsec/pixel; tracking settle 8 s, arrival 0.5°, RMS 3 arcsec, stable 1 s with 0.75 s extra settle, six samples, 20 s timeout and 0.5 s post-exposure buffer. Unlike the C# timeout fallback, an unsettled position fails rather than taking an exposure anyway. Travel limits and fresh enabled axes are checked by the existing mount service.

New/rerun/delete/load operations archive previous observations. Settings and the grid persist in `runtime/pointing-workbench.json`; real observations remain in `runtime/calibration-points.json`, camera images in `runtime/captures`, fitted files in `runtime/models`, and analysis exports in `runtime/pointing-analysis-*.json`. Operator actions and job results use the existing engineering audit database.

Seeing/tracking uses a fixed field and measures a reference star from real FITS frames. Image FWHM includes optics and atmospheric seeing. The pointing stability graph measures centroid repeatability within each position, not absolute pointing accuracy; absolute errors require solved observations and model residuals. Neither workflow substitutes generated observations when a camera or star detection is unavailable.

Validation: `tests/test_pointing_workbench.py` covers grid retention, pause/mount stop, camera guards, conflicting operations, FITS measurements and both native fit/export/reload backends. Synthetic fixtures are confined to offline tests. `tests/pointing-workbench-ui.cjs` tests all three tabs, candidate review and mobile layout using intercepted operations; it sends no mount or exposure commands. `tests/pointing-location-ui.cjs` checks map clicks, editable coordinates, draft persistence via intercepted requests, transitions and mobile layout.

The location step now advances to NTP time-source selection. Find One measures Cloudflare and Thai/Asia NTP Pool services from the mount server (one resolved endpoint per service, up to three samples). Results rank by estimated root distance plus sample jitter, with round-trip delay, clock offset and stratum shown separately. Custom IPs, hostnames and root URLs are accepted; all queries use UDP 123. Unsynchronized, unmatched and Kiss-o'-Death responses are rejected. Next requires a successful measurement within five minutes and saves the selected source to the workbench draft. This step does not discipline the Windows clock or alter astronomical timestamps.

Observatory setup persists in `runtime/observatory-config.sqlite3` (`NARIT_CONFIG_DB` override). SQLite settings hold GPS and the selected NTP measurement; the telescopes table holds multiple OTA specifications with stable IDs and creation/update UTC timestamps. Existing GPS/NTP JSON drafts migrate once; database values take precedence on restart. The NTP step now advances to the telescope list with Create First Telescope, Add Telescope and Edit. Only name and native focal length are required; aperture, optical design, manufacturer/model, reducer/Barlow factor, back focus, obstruction, tube length, weight, mount position and notes are supported. Native/effective focal ratio derives from aperture and focal length. No saved OTA specification triggers mount motion or changes runtime camera controls.
