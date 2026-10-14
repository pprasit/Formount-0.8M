# NARIT Python Mount Service

## Imaging equipment after Telescope

Saving a new Telescope opens Imaging equipment. Select its OTA, scan USB cameras on the controller, connect a device, check the sensor pixel size, and save exposure, binning, gain and notes. A test exposure is optional and only runs when Capture test frame is clicked. The active imaging profile is stored in `observatory-config.sqlite3`; plate scale includes the OTA optical multiplier and binning and is applied to calibration settings. A saved profile never automatically opens a camera on startup.

| Camera family | Controller SDK | Acquisition and readout |
| --- | --- | --- |
| ZWO ASI | `ASICamera2.dll` | Native device IDs, advertised bins/formats/control limits, RAW16 snapshot, successful exposure status before download |
| FLI MicroLine / ProLine / compatible CCD | `libfli64.dll` | USB camera domain, visible sensor area, aligned binning, 16-bit row readout |
| Andor CCD / EMCCD | `atmcd64d.dll` (SDK2) | Camera handle selection, image/single-scan/internal-trigger modes, actual acquisition timing, `GetAcquiredData16` |
| Andor USB sCMOS | `atcore.dll` (SDK3) | Real USB cameras only, Mono16 AOI, aligned queued buffer, bounded waits, row stride, stop and flush |

FLI Kepler uses a separate vendor SDK and is **not covered by the libfli adapter**. SDK3's SimCam and non-USB devices are excluded. A missing DLL, USB driver, dependency or incompatible bitness is reported per vendor without preventing other vendors from scanning. The installed SDK2 in this controller is under `C:\Program Files\Andor SDK`; SDK3 was absent during verification. The controller scan found no attached cameras, so physical acquisition acceptance is still required.

Install vendor USB drivers and the matching 64-bit SDK on the controller. Existing repository DLLs are reused. Custom install locations can be supplied through `NARIT_ZWO_SDK_DIR`, `NARIT_FLI_SDK_DIR`, `NARIT_ANDOR2_SDK_DIR`, `NARIT_ANDOR3_SDK_DIR`; SDK2 detector initialization files may use `NARIT_ANDOR2_CONFIG_DIR`. Changes to API/native adapters require a service restart.

Exposure and readout run on the existing single camera worker. Overlapping camera jobs and calibration are rejected. Abort also invalidates queued captures, checks cancellation during waits/readout, and prevents cancelled images being saved. A native USB call can still delay cancellation until the vendor driver returns; this is not a hard real-time guarantee. Use a reliable USB cable and powered supply, close other camera applications, and rescan after reconnecting a device.

Numeric gain is exposed for ZWO using its reported limits. FLI CCD and Andor named/preamp/EM gain controls are not interchangeable with that gain value; their numeric field is disabled and native gain settings remain unchanged. Imaging now groups settings in Camera Control and Cooling tabs. The Cooling tab reads SDK2 sensor temperature, cooler state and camera-reported target limits every two seconds while idle; Apply target and Cooler On/Off use the serialized native camera worker. Temperature updates and cooling changes pause during acquisition. Fan speed and EM gain remain under vendor controls. SDK2 refuses disconnect below -20 °C rather than switching a cold sensor off; warm it using vendor controls first. Dark acquisition requires a physical shutter or a covered sensor. Internal SDK2 shutter timing is read from the camera; external shutters require a separate configuration. ZWO ROI is cropped to the SDK's 8-by-2 alignment, with the additional USB2 ASI120 area constraint. FLI parameters unsupported by an individual model fail through its SDK. SDK3 preserves its current AOI and reads the returned dimensions and stride. FITS files contain raw unsigned 16-bit pixels and capture metadata; PNGs are contrast-scaled previews. Colour ASI frames retain their Bayer data, not a debayered science image.

Vendor references: [Andor SDK2 manual](https://andor.oxinst.com/downloads/uploads/Software%20Development%20Kit.pdf), [Andor SDK3 manual](https://andor.oxinst.com/downloads/uploads/Andor_SDK3_Manual.pdf), [FLI support and separate CCD/Kepler SDKs](https://www.flicamera.com/support), [ZWO SDK downloads](https://www.zwoastro.com/software/product-sdk/). ZWO ABI and ROI rules were also checked against the repository's `Narit-Mount-Lib/Docs/ASICamera2 Software Development Kit.pdf`. FLI ABI was checked against the FLI-authored [libfli header](https://github.com/RTS2/fliusb/blob/master/libfli/libfli.h).

Verification: `tests/test_camera_sdk.py` checks native-call contracts, RAW16 pixels/FITS, error cleanup, SDK3 padded rows and buffer identity, queued aborts, imaging persistence, plate scale and audit events. `tests/imaging-workbench-ui.cjs` checks the wizard with mocked device/config writes and desktop/mobile layout; it never captures a physical frame.

## Non-celestial tracking

เมนูใต้ Astronomy มี 4 กลุ่ม: Solar system, Satellites, Asteroids และ Comets.
เลือกวัตถุแล้วแสดง UTC, Az/Alt, apparent RA/Dec, อัตราเคลื่อนที่ (difference 1 วินาที), encoder targets และขอบเขตการเดินทางอัตโนมัติผ่าน `/ws/astronomy/moving`; ไม่มีปุ่ม Preview ใน panel นี้.
ตั้งรอบคำนวณเป้าหมาย 5 ms. แต่ละ stream มีตัวอย่างค้างส่งได้หนึ่งตัวและรอ browser acknowledge ก่อนรอบถัดไป จึงไม่สะสมข้อมูลเก่า. แสดงช่วงเวลาจริงต่อ sample ใน panel; ไม่รับประกัน hard real-time บน Windows/Python/เครือข่าย. Browser วาดค่าตาม refresh rate หน้าจอ และหยุด stream เมื่อออกจาก panel หรือซ่อนแท็บ. เมื่อสลับวัตถุจะยกเลิก stream เก่าเพื่อกันค่าเก่าทับชื่อใหม่.
Slew และ Tracking ใช้ guards เดิม: fresh/enabled axes, settle ภายใน 0.25°, error ไม่เกิน 0.5° และ tracking rate ไม่เกิน 1°/s.

- Solar system ใช้ PWLib Planets และ RA/Dec → LMST → AltAz เช่นเดียวกับ C# `PlanetTrackingControl`. รัน `./setup-satellite-runtime.ps1` ครั้งแรกเพื่อติดตั้ง NLog dependency.
- Satellites ใช้ PWLib SGP4 และ topocentric conversion แบบเดียวกับ `SatelliteData.cs`. รัน `./setup-satellite-runtime.ps1` ครั้งแรกเพื่อติดตั้ง NLog dependency. นำเข้า `.tle`/`.txt` หรือวาง TLE ที่มี checksum ถูกต้อง; 6 หมวดจาก C# คือ NARIT-ACTIVE, VISUAL, STATIONS, STARLINK, GPS-OPS และ CUBESAT. Catalog ที่นำเข้าอยู่ในหน่วยความจำของหน้าเว็บจนรีเฟรช. ยังไม่มีการเชื่อม NARIT API/MongoDB อัตโนมัติ.
- Asteroids และ Comets port สมการจาก `SmallBodyEphemerisCalculator.cs`: Kepler orbit, ลบตำแหน่งโลก และหมุน ecliptic เป็น equatorial. ใช้ published orbital elements; ไม่มีค่า catalog สมมติ. Comets รองรับเฉพาะ `0 ≤ e < 1`. แบบจำลองประมาณนี้ไม่มี perturbations/light-time corrections ตาม C#.

ต้อง restart Python service เพื่อโหลด API/model ใหม่หลังอัปเดตโค้ด. ตรวจ panel โดยไม่ควบคุมฮาร์ดแวร์ด้วย `tests/moving-workbench.cjs` บน test service แยก.
Dropdown ทั้งเว็บใช้ `web/selects.js`/shared CSS: คง native select เป็นข้อมูลของฟอร์ม, รองรับรายการโหลดภายหลัง, ค้นหารายการยาว, keyboard navigation และ popover ภายใน dialog.

กราฟ Horizon Altitude ใช้หน้าต่างย้อนหลัง 1.5 วันถึงล่วงหน้า 1.5 วัน: X เป็นวัน/เวลาไทย (ICT), Y เป็นมุมก้ม–มุมเงย −90°..90°. เส้น 0° คือ geometric horizon (ไม่รวม atmospheric refraction), มีเส้น Now และ marker ขึ้น/ตกพร้อม tooltip. ตัวอย่างทุก 10 นาทีสำหรับ Solar system/small bodies และทุก 20 วินาทีสำหรับ satellites; หาช่วงตัดขอบฟ้าละเอียดถึง 1 วินาที. คำนวณผ่าน `/api/astronomy/moving/visibility` บน worker แยกและ PWLib instance แยกจากค่าสด, ยกเลิกเมื่อเปลี่ยนวัตถุ. Panel แสดงเวลาขึ้น/ตกครั้งถัดไปและจัดรายละเอียดให้พอดีกรอบ desktop; mobile เลื่อนทั้ง workspace ได้.

Python/FastAPI backend + dashboard สำหรับ Elmo Platinum drive หนึ่งตัว: AX1 Azimuth, AX2 Altitude, AX3 Rotator, AX4 M3.

## ระบบที่ใช้งานอยู่

- URL: https://forkmountpc.tailc95dd4.ts.net/ (Tailscale tailnet เดียวกัน, ไม่ต้องใส่พอร์ต)
- Local: http://localhost:8765 · API docs: http://localhost:8765/docs
- ข้อมูลจริงจาก COM3, serial 25262718, host baud setting 921600
- Polling target 5 ms / 200 Hz: อ่าน PX, VX, IQ ครบ 4 แกนทุก acquisition cycle
- MO อ่านทุก 200 ms; อ่าน SN[4], UM และ FC ratios (ยกเว้น FC[3]/[4] ที่สงวนไว้) เมื่อเชื่อมต่อ
- ตรวจจาก drive จริงแล้ว FC ทุกแกนเป็น 1/1 และ framing เป็น echoed command + CR + value + semicolon
- ไม่มีข้อมูลจำลองใน production และไม่มี fallback ไป simulation หาก hardware ล้มเหลว

## เริ่มระบบ

PowerShell จาก repository root:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e './python-mount-service[test]'
.\python-mount-service\start.ps1
```

Background PID/log อยู่ใน runtime/. ตั้งค่าถาวรก่อน startup ใน config.json (port, baud, serial_number, poll_interval_ms). Dashboard Drive connection ปรับค่าเหล่านี้ตอน reconnect ได้; ค่าที่ปรับในหน้าเว็บมีผลต่อ connection ปัจจุบัน. Host baud setting เป็นการตั้งค่า COM; ไม่เขียน baud configuration ถาวรลง drive. USB virtual COM อาจไม่ได้เปลี่ยน bandwidth ตาม baud setting.

Foreground จากโฟลเดอร์นี้:

```powershell
..\.venv\Scripts\python.exe -m uvicorn mount_service.api:app --host 127.0.0.1 --port 8765
```

## โครงสร้าง

```text
Dashboard / REST clients
        │ commands + WebSocket sample batches
FastAPI api.py
        │
MountService service.py — axis state, validation, bounded history
        │
SerialElmoDrive transports.py — one serial owner, acquisition thread
        │
        AX1 / AX2 / AX3 / AX4
```

- domain.py: แยก mapping, encoder conversion และหน่วยต่อแกน
- Acquisition thread ใช้ perf_counter timestamp ความละเอียดสูงและ deadline 5 ms; ไม่พึ่ง browser หรือ asyncio timer ในการกำหนดจังหวะ serial
- Serial อ่าน reply เป็น chunks เพื่อลด Windows calls ต่อ byte; มี request ค้างเพียงหนึ่งรายการ
- Dashboard แสดง measured acquisition Hz, average read time, target interval; hover ที่ read time แสดง P95 และ overrun count
- เป้าหมาย 200 Hz ไม่ใช่ hard real-time guarantee. เมื่อรอบอ่านเกิน 5 ms ไม่ queue คำถามสะสม; แสดง rate ที่ทำได้จริง
- หน้าเว็บเปิด WebSocket ทันที; โหลด history เบื้องหลังและ merge กับ sample ใหม่ โดยไม่รอ history ก่อนแสดงค่าปัจจุบัน. History response บีบอัด gzip ใน worker thread และ frontend ใช้ font ในเครื่องเพื่อไม่รอ external stylesheet
- WebSocket ส่งตาม acquisition interval (เป้าหมาย 5 ms) โดยแนบทุก sample; กราฟวาดทุก sample โดยไม่ลดจุด และ redraw ด้วย requestAnimationFrame ตาม refresh rate ของจอ
- History เก็บสูงสุด 24,000 frames (~120s ที่ 200 Hz). CSV มี sample acquisition จริงครบทั้ง 4 แกน
- กราฟเลือก Position / Velocity / Current ต่อแกน, time window, pause/live, hover, CSV และ responsive mobile
- ข้อมูลอ่านเสียเก็บ last known value พร้อม valid=false; ไม่มีการเติมค่าปลอมเมื่อ disconnect

### HTTP API

| Endpoint | หน้าที่ |
|---|---|
| GET /api/state, /api/health | สถานะระบบและการเชื่อมต่อ |
| GET /api/history?seconds=60 | acquisition frames พร้อม high-resolution relative timestamps |
| GET /api/history.csv | CSV ทุกแกนพร้อมหน่วยและ quality |
| POST /api/connect | hardware connection, expected identity, host baud และ polling interval |
| POST /api/disconnect | หยุด worker แล้วปิด serial |
| WS /ws/telemetry | current state + samples batch |
| POST /api/axes/{axis}/command, /api/stop | Enable/Disable/Stop/Jog/Goto จริง และ Stop All |

ตัวอย่าง connection: {"mode":"hardware","port":"COM3","baud":921600,"serial_number":"25262718","poll_interval_ms":5}.

## หน่วยและขอบเขต

Encoder Az/Alt/Rotator 2^32, M3 2^18 ตาม saved Fork-Mount profile. PX/VX แปลงจาก count / counts-per-second เป็น mechanical degrees / degrees-per-second Az/Alt ไม่ wrap มุม; Rotator/M3 แสดง normalized 0–360°. ตรวจ FC=1/1 ก่อนเริ่ม polling. ตำแหน่งนี้ยังไม่ได้รวม astronomical alignment, zero offsets หรือ pointing model.

Azimuth 45–700°, Altitude 0–90° ตามโปรไฟล์เดิม. Rotator และ M3 ใช้ขอบเขต **0–359°** ตามผู้ใช้ยืนยัน 2026-10-08. ค่าดิบของ Rotator ที่ -29.26° แสดง normalized 330.74°; Goto คง encoder turn ปัจจุบันเพื่อไม่ข้ามจุด 0/360 โดยไม่ตั้งใจ. ค่าตำแหน่งจริงนอกขอบเขตไม่ถูกตัดทิ้ง; Jog อนุญาตทิศที่กลับเข้าสู่ช่วง.

## Live control

เปิด Enable/Disable/Stop/Jog/Goto ทุกแกนและ Enable All. Startup ไม่ Enable หรือเคลื่อนแกนโดยอัตโนมัติ. Serial worker เป็นเจ้าของพอร์ตทั้ง acquisition และ commands; Stop มีลำดับก่อนคำสั่งเคลื่อนที่และยกเลิกคำสั่งค้าง โดยไม่ยกเลิก Stop อื่น. Jog ต้องต่ออายุ lease; worker ส่ง ST เมื่อหมดอายุ แม้ browser/API event loop หยุดตอบ. Telemetry หรือ MO ผิดปกติ และการ Jog ออกนอกขอบเขตจะสั่ง ST. หากไม่รับ acknowledgement ให้ retry และรายงานข้อผิดพลาด ไม่รายงานว่าหยุดแล้ว.

Enable ส่ง ST, ตั้ง JP/JV=0 และ MO พร้อมอ่าน MO ยืนยัน. UM=5 ใช้ JP+BG สำหรับ Jog และ ST/SP/PA/BG สำหรับ Goto; UM=2 รองรับ JV+BG เฉพาะ Jog. ไม่เปลี่ยน UM อัตโนมัติ. Drive จริงทุกแกนอ่าน UM=5. คำสั่งมอเตอร์ทดสอบด้วย transport mock; ไม่ได้ Enable หรือเคลื่อน hardware ระหว่างพัฒนา.

## Astronomy, Pointing, Camera และ Calibration

- astronomy.py ใช้ Astropy: J2000/ICRS, geometric AltAz, solar-system targets, apparent sidereal time และ parallactic angle. Site จากโปรไฟล์เดิม 18.853823 N / 98.958353 E / 350 m; pressure=0 ไม่เพิ่ม refraction โดยสมมติสภาพอากาศ. BSC5 จริง 9,096 ดาว; ไม่มี catalog จำลอง. Proper motion ยังไม่ propagate. RA/Dec จาก encoder ที่ไม่มี fitted model เป็น geometry ตาม offset เดิม ไม่ใช่หลักฐานว่า telescope alignment ถูกต้องแล้ว.
- Preview คำนวณ encoder target และเลือก azimuth turn ที่อยู่ในขอบเขต; Slew ต้องเปิด Az/Alt และมี telemetry ใหม่. Tracking คำนวณ feedforward ทุกประมาณ 50 ms พร้อม correction, lease 600 ms, guard error 0.5° และ velocity 1°/s; ต้อง slew/settle ภายใน 0.25° ก่อนเริ่ม. ยังไม่ได้ทดสอบ tracking บนท้องฟ้าจริง.
- pointing.py ใช้ PointXP instance เดียวผ่าน Python.NET/.NET Framework และ DLL ของ repository. Legacy .pxp load แยกจาก experimental CoreDirect fit. Fit ต้องมีอย่างน้อย 9 solved points, ใช้ J2000 ที่ plate solve ได้ + encoder ที่วัดระหว่าง exposure. Core.EncToAltAz ใช้ runtime correction. Wrapper ApparentToEncoder ของ DLL นี้คืน out=(0,0); ตรวจ wrapper แล้ว fallback Core.AppToEnc พร้อม round-trip validation. SaveFile ขาด coefficient section จึงแนบ Model Terms จาก GetMountError จริง; test fit/export/load/inverse ผ่าน. ไม่ auto-load โมเดลทดสอบ.
- camera.py ต่อ ZWO/FLI SDK เดิม, สแกนและเปิดเมื่อผู้ใช้สั่ง, RAW16 exposure/abort, FITS และ PNG preview ของภาพจริง. ณ ตรวจล่าสุดไม่มี camera connected จึงยังไม่ทดสอบ exposure จริง. FLI gain ไม่ถูกปรับด้วยช่อง gain (ใช้เฉพาะ ZWO).
- solver_worker.py รัน PWLib ใน subprocess แยก มี timeout. ใช้ UCAC4_PATH หรือ auto-detect ~/Documents/Kepler/UC4/Index.UC4 ที่พบจริงในเครื่อง. ทดสอบกับ D:/Test PlateSolve/Point4.fit จริง: matched 14 stars, RMS 0.2455 arcsec. ไม่เอาไฟล์เก่าหรือผลทดสอบนี้ใส่ calibration points ของ operator.
- Calibration ใช้ Capture → Solve & add point → เลือก points → Fit → Download .pxp. Snapshot encoder ก่อน/หลัง exposure; ปฏิเสธ point หาก encoder invalid หรือเคลื่อนเกิน 0.02°. Pipeline นี้เป็นการเก็บ point ทีละตำแหน่ง; ยังไม่มี automated multi-star slew sequence. ผล capture/points/models เก็บใน runtime/.
- Observatory jobs แยก thread/process จาก serial worker และรายงาน complete/failed ใน WebSocket; ไม่มีผล plate solve หรือ camera data จำลองใน production.

API เพิ่ม: POST /api/enable-all, /api/astronomy/{preview,goto,tracking/start,tracking/stop}, /api/pointing/{load,fit}, /api/camera/{scan,connect,disconnect,capture,abort}, /api/calibration/solve. ดู schema รวมที่ /docs. GET /api/observatory, /api/astronomy/stars, /api/calibration/points และ download endpoints.

## Tailscale

HTTPS Serve proxy ไป http://127.0.0.1:8765 และทำงาน background:

```powershell
& 'C:\Program Files\Tailscale\tailscale.exe' serve status
& 'C:\Program Files\Tailscale\tailscale.exe' serve --bg --yes http://127.0.0.1:8765
```

ไม่มี Tailscale Funnel หรือ public publishing. start.ps1 ยัง bind 0.0.0.0:8765 เพื่อรองรับ local access; prototype ไม่มี application login และมี same-origin write checks.

## Verification

```powershell
..\.venv\Scripts\python.exe -m pytest -q
node --check mount_service/web/app.js
node tests/dashboard-smoke.cjs
node tests/startup-latency.cjs
node tests/workbench-smoke.cjs
```

Browser test ใช้ Playwright + Edge headless ใน profile แยก; ระบุ MOUNT_PLAYWRIGHT_MODULE หาก package ไม่ได้อยู่ใน local node_modules. Hardware branch ตรวจ UI/ข้อมูลจริงและไม่ส่ง motion หรือ disconnect drive. ภาพอยู่ใน runtime/.

วัดหลังปรับ chunked serial read วันที่ 2026-10-08: 200.0 Hz, mean read 2.96 ms, P95 read 3.373 ms; actual frame spacing median 5.001 ms / P95 5.802 ms ในช่วงวัดสั้น. Rate/jitter เปลี่ยนตาม load; ใช้ค่าที่แสดงบน dashboard เป็นหลัก.

## Live Telemetry workspace (2026-10-09)

Live Telemetry opens Combine axes by default. The sky diagram occupies ~2/3 of the available width; targets and independent Az/Alt speeds use the remainder. Tabs select one full-height axis chart: Azimuth, Altitude, Rotator, M3. Main pages, sidebar and graph panels fit the viewport without vertical scrolling; calibration points paginate six rows. Sky-map clicks select a target without issuing motion.

POST /api/axes/combined/goto accepts azimuth, altitude, azimuth_speed and altitude_speed. Both axes are validated before writing, both ST/SP/PA sequences are staged before the two BG commands are issued in one serial job. This is coordinated dispatch, not a hardware synchronized interpolation guarantee. Any sequence failure attempts ST on both axes. Mock tests cover staging, failure rollback and full validation.

GET /api/inputs returns genuine IP[1] raw bitfield, active bits, age/quality and the legacy CW/CCW interpretation. This firmware accepts IP[1] and rejects IP[2]; IP indexes are not treated as four axis IDs. Inputs are sampled at 200 ms without changing input configuration. At inspection IP[1]=65537, active bits 0 and 16. Legacy AzimuthManager uses bit 16 as CW and 17 as CCW; physical wiring/polarity is unverified, so these labels are marked explicitly and do not override motion limits. Both bits can be active, unlike the old whole-value equality check. Failed/stale reads show UNKNOWN.

Mechanical region uses unwrapped encoder turns: turn 1 (0–360), turn 2 (360–720), with permitted segments 45–360 and 360–700. Altitude remains 0–90. The diagram projects sky azimuth modulo 360, preserves the mechanical value and displays both travel arcs. Position below 45 (currently ~12.23) is reported outside configured travel rather than silently changed. Existing SkyPositionMap and AzimuthManager were consulted. The requested fire-detector region implementation was not located in the available projects/tasks; sensor-assisted turn inference is intentionally not guessed.

Browser regression: node tests/telemetry-tabs.cjs checks input telemetry, default tab, 2/3 layout, one visible axis graph, form payload with motion requests intercepted, isolated navigation and fit at 1902x934, 1366x768 and 1280x720. No motor commands are sent to hardware by these checks.

Graph rendering keeps bucket minima/maxima and last samples at viewport resolution to avoid long canvas paths blocking Pause/Resume. Axis scaling still uses the full sample window; acquisition history and CSV remain unmodified.

### Hardware workbench (Platinum)

Drive connection now opens **Connect to Hardware**, with Connection, Drive, Motor,
Encoder and Commutation tabs and a selector for AX1–AX4. Motor parameters MP[1–6],
current limits CL[1]/PL[1–2], encoder settings CA[18]/SA[1–2], user-unit scaling FC[1–2]/[5–12], and drive mode UM
support individual RAM writes with a fresh MO=0 check, expected-value check and
readback verification. Current ceilings are read from MC[2–4]. SV is an explicit,
drive-wide flash save after verifying all four motors are disabled and stopped.
The Platinum EAS helper uses a 6000 ms SV timeout; this service uses the same reply
budget and an 8 second queue acknowledgement budget. Flash save can pause acquisition.
There is no raw serial command endpoint. Hardware writes are logged in
`runtime/hardware-audit.jsonl`; failed acknowledgement is not proof a write failed,
so read again before retrying. Stop axis remains accessible inside the dialog.

Encoder configuration has three subsections: Resolution & feedback, User-unit scaling,
and Sensor protocol. CA[18] is commutation counts, Sx[7] is the sensor single-turn
bit count, and MOUNT_CPR is the service's counts per mechanical axis revolution.
These are deliberately separate values: changing CA[18] does not silently alter the
mechanical conversion. Count-field bit presets fill 2^bits; Write applies the value.
MOUNT_CPR is saved in config.json under axis_resolutions and survives reconnects.
Position raw counts convert using MOUNT_CPR, while motion user-unit references use
FC[7]/FC[8] × FC[6]/FC[5] × FC[2]/FC[1]; velocity additionally uses FC[9]/FC[10].
FC[1] and FC[2] are resolution numerator and denominator, not position vs velocity
factors. PX and VX remain raw counts and counts/second. FC[3]/[4] are reserved.

Sensor sockets S1–S8 are shared drive resources, not AXn commands. Type, serial
clock, single/multi-turn bits, reduced bits, frame layout, filter, port and counting
direction can be configured for the supported encoder types. The workbench currently
covers Quadrature, Analog Sin/Cos, BiSS, Panasonic, EnDAT and SSI; it does not claim
every EAS II sensor type. Port/direction writes preserve unrelated Sx[2] flags.
All four motors must be disabled and stopped for shared socket writes. Generic
BiSS/SSI frame bounds are checked; Renishaw BiSS auto-format is handled separately
because the manual says it derives framing parameters from sensor resolution.
Socket selection is explicit; match it to the configured SA[1]/SA[2] assignments.
Readback uncertainty in scaling/feedback/mode configuration blocks motion until
reconnect re-reads configuration. UM modes 0/1/2/3/5/6/7 can be configured;
mount Enable/Jog supports only 2/5 and Goto requires 5.

Commutation writes CA[7]/[16]/[17]/[20]/[25] are paused at the operator's request.
CA[18] remains editable in Encoder, as explicitly requested. Its change, like
changing MP[4], can reset commutation. The commutation tab only reads settings and
shows real position/velocity/current traces, with a 10 second capture to CSV.
It does not run EAS Auto Phasing or display experimental electrical-angle data.
No commissioning values were written to the physical drive during development.

Sources checked on this machine:
- Installed EAS II `Configuration/PlatinumCommandReference.xml`.
- Installed EAS II `NetHelp/Software Manual Pdfs/MAN-P-CommandRef.pdf`.
- Installed EAS II `NetHelp/Documents/pcancommutationparameters.htm`,
  `pmpmotorparameters.htm`, `pclcurrentlimitparameters.htm`,
  `pplncurrentpeaklimit.htm`, `pmcmaximumcurrent.htm`, `psvsaveparameters.htm`,
  and `runningcommutation.htm`.
- Installed EAS II Platinum helper `DownloadParamsTxtPlatinumWorker.cs` (SV timeout).
- [Elmo commutation options](https://www.elmomc.com/capabilities/servo-technology/servo-tools/commutation-options/)
  and [Elmo resource center](https://www.elmomc.com/products/application-studio/download-resource-center/).

Read verification against COM3 / SN 25262718: encoder, scaling, motor, drive and sensor parameters were read
successfully on each of the four axes. All four position sockets use Renishaw BiSS;
S1/S2/S3 report 32 single-turn bits and S4 reports 18 bits. Automated writes and flash saves were tested
only against an offline mock, not the physical motors. Tests cover disabled-state
checks, stale-value rejection, current ceilings, command allowlisting and global SV.

Additional sources: EAS Platinum pfcscalingfactors.htm equations, pumnunitmode.htm, psasocketfunction.htm and ps1s8pf1f8sensorsocketparameters.htm. Validation: 47 backend tests, editable encoder UI tests with writes intercepted, and the real-hardware dashboard smoke test.


### Live command verification, 2026-10-09
Enable now sends ST, MO=1, checks MO and waits for SO=1 before JP/JV=0. Platinum rejects JP while the motor is off. Preparation failure disables MO again. Live Enable was attempted on all four axes: MF=536870912 and EE[5]=EC=229 (Safety state detected during motor enable). Stop and Disable acknowledged on all four axes. Jog/Goto hardware verification remains blocked by drive safety state. Software regression suite: 55 passed.


Telemetry transport (2026-10-09): acquisition remains at the configured 5 ms target. WebSocket batches every 1/60 s with every acquired sample; plotting retains all points. Limiting message frequency avoids unnecessary browser/service workload. Serial responses terminate exactly at semicolon. Live Azimuth Enable held 15 s: 190-193 Hz, telemetry age 4-9 ms. MO state verification allows 100 ms for asynchronous transitions.


Satellite sky uses the same 3D horizontal projection and view controls as Astronomy.
NARIT-STARLINK is the initial satellite catalog. Categories load through the server
from the original NARIT TLE API; configure `NARIT_TLE_TOKEN` in the server environment,
or the service reads the existing credential from the local C# NaritTLEService source.
The credential is never returned to the browser. Catalogs are cached for one hour.

The satellite layer uses accelerated Vallado SGP4 array propagation, followed by
Astropy TEME-to-ITRS conversion and observer-relative east/north/up coordinates.
This is a geometric local sky sphere, not an Earth-centered orbit display. Below-horizon
satellites are dimmed and become visible when the sphere is rotated. The selected
object retains the existing PWLib target-coordinate/mount-preview pipeline.
The independent catalog stream targets 100 ms with one outstanding frame and no
queued refresh backlog. Its footer reports measured frame interval and calculation
and worker CPU time. On this workstation, 10,659 Starlink TLEs took roughly 21–33 ms
per warmed-up calculation; initialization and other concurrent work can take longer.
Selecting a satellite draws forward to its first setting horizon crossing, then
continues for two orbital periods derived from TLE mean motion. A selection below
the horizon waits for the next rise and setting. The horizon search is capped at
24 hours for objects without a setting crossing. Samples adapt to orbital period
(2–30 seconds); below-horizon segments stay dashed when rotating the sphere.

Engineering audit is persisted in `runtime/engineering-audit.sqlite3` (SQLite WAL).
Override the destination with `NARIT_AUDIT_DB`. Event log displays server UTC times
in Asia/Bangkok (ICT); browser event time is retained separately. Clicks across all
pages, input changes, canvas drags, selection events, browser errors, socket lifecycle,
command parameters/status/results, hardware verification, mount events and background
job completion/failure are recorded. HTTP request/session/action IDs connect clicks
to commands; a command acknowledgement is separate from a later job outcome.
Browser events queue locally while offline and retry with deduplication IDs. Password
fields and credential keys are excluded/redacted. Audit recording failure does not
block emergency motion controls. Successful telemetry polling/individual satellite
frames are not stored. Read history with `GET /api/audit/events?limit=200&before=ID`
and optional `session_id`/`action_id`; records persist across service restarts.
Satellite stream failures record stage and exception; interrupted streams reconnect
automatically and restore the catalog note after the first successful frame.

Asteroids and Comets use the same 3D sky sphere and compact split panel as Satellites.
The server loads up to 500 numbered asteroids or numbered elliptic periodic comets
from NASA/JPL SBDB, stores the catalog for 24 hours, and refreshes plotted positions
at 30-second intervals. A sky click or searchable dropdown selection populates the
live target fields and orbital metadata; orbit editing remains available under
“Orbital elements / manual target”. Existing Slew/Tracking commands and their guards
are retained. The plot and live selected target share the existing approximate
Kepler model (no perturbations or light-time corrections), not JPL Horizons precision.
Only elliptic comet orbits (`e < 1`) are included, matching the current target model.
If JPL is unavailable, a saved catalog may be reused and explicitly marked cached.
Source API: https://ssd-api.jpl.nasa.gov/doc/sbdb_query.html

Imaging setup uses document scrolling without a nested panel scrollbar. Pixel size is filled from sensor metadata when available (manual fallback). Binning choices 1x1 through 8x8 are checked against SDK capabilities; unsupported choices are disabled. Exposure accepts decimal seconds. Capture test frame switches to Abort exposure while acquisition/readout is active.

When USB discovery finds no physical cameras, Simulated camera is available for explicit connection and imaging setup. Its preset is a 6248x4176 monochrome sensor with 3.76 micrometre pixels, gain 0-600 and binning 1-8. Images now use the existing C# `PWLib.FakeStarField` with the installed UCAC4 catalog. Capture converts fresh encoder Alt/Az to ICRS/J2000 using the saved site and UTC exposure midpoint, derives unbinned scale from the selected telescope focal length and sensor pixel size, and renders catalog positions with magnitude-dependent Gaussian star profiles, charge binning and sensor noise. Catalog reads run in a cancellable isolated process. Missing catalog or pointing context fails explicitly; no random-star fallback or fake successful solve is used. FITS labels simulation and catalog/star count, site, encoder and rendering parameters, without adding WCS solution keywords. PWLib plate solving still reads the image independently.

The simulator uses catalog J2000 geometry (`Epoch=2000`) to match the solver; it does not model current stellar proper-motion displacement, atmospheric refraction, optical distortion, field drift or mount pointing errors. Rotation uses a preset camera orientation plus geometric parallactic angle. Passing this simulation validates the image/solve pipeline, not a physical mount calibration. Calibration settings store the unbinned scale, while imaging summaries and FITS report scale after binning.

Opt-in installed-library integration: `NARIT_NATIVE_CATALOG_TESTS=1 python -m pytest tests/test_catalog_simulation.py -q -s`. It generates catalog FITS and invokes the real solver at an ordinary field, RA wrap and high declination (including rotation/binning). It does not move hardware or save/activate a model. The C# UCAC4 reader and `PWLib.FakeStarField` were inspected; source for the earlier synthetic UI/generator is absent from the current checkout.

Camera preview is now a bounded 8-bit PNG (long side at most 1280 pixels), with peak-preserving pooling so small stars survive reduction. Display stretching is separate from RAW16 FITS and plate solving. Profile percentile/asinh parameters come from C# `HistogramStretchProfile`: Auto (0.5/99.95%, strength 8), Low (1/99.98%, 4), Medium (3/99.95%, 7), High (8/99.90%, 12), BrightStar (1.5/99.97%, 5), Moon (0.2/99.70%, 1.4), Planet (0.05/99.99%, 1.2), DeepSky (12/99.85%, 18). The UI defaults to Auto; the preview helper retains the other presets for reuse. Auto adds robust median/MAD sky clipping to avoid a grey noise background in sparse fields. This follows display-only screen-stretch behavior described by MaxIm DL; it does not claim an identical proprietary algorithm: https://cdn.diffractionlimited.com/help/maximdl/HID_VIEW_HISTOGRAM.htm

Saving imaging equipment opens the calibration workspace; a test exposure is optional. The left Alt/Az sky chart shows fresh encoder pointing, planned positions and solved/failed observations. Configure an exact total (1-288), azimuth interval and altitude interval. Start/Stop shares one button; Reset archives measurements and rebuilds the plan while preserving the currently active model. Motion still requires connected camera and enabled, valid mount axes. C# uses Alt/Az for coverage; saved plate-solve/model coordinates remain J2000 RA/Dec.

The right panel shows the latest preview, C# stretch presets and live black/white sliders. A bounded uint16 sensor preview (long side at most 1280) is downloaded once per capture and stretched locally; slider movement makes no additional capture/download. Auto uses robust sky clipping. Raw FITS and plate-solve input are untouched. The existing workbench performs slew, settle, capture and independent plate solve; only successful solutions become model observations. Desktop/mobile UI checks mock every calibration command so they cannot move the physical mount.

Calibration Map and Captured Points are nested tabs in the left card. Captured markers and table rows select a historical image; Latest frame resumes live updates. Start/Stop and Reset have wider click targets. Captured Points retains successful and failed calibration captures across resets. Imported points without image files remain visible on the map but cannot show a missing image.

Each completed exposure archives capture/device/telescope metadata, FITS header cards, a bounded uint16 preview, an Auto PNG and all eight stretch preset parameters in `runtime/camera-images.sqlite3` (override with `NARIT_IMAGE_DB`). Calibration captures add plan ID, target Alt/Az, solve result/point and failure details. Per-image display profile, black/white points and asinh strength are saved after a 450 ms slider debounce. History loads these cached pixels/parameters rather than rereading FITS or recalculating percentiles; up to eight decoded previews are also cached in the browser. Existing FITS files are imported once on first access. Reset archives measurements without deleting image history. Metadata and preview blobs live in SQLite; original scientific FITS files remain separate and untouched.

Camera Preview includes an unchecked Detected stars toggle. Circles use full-frame PlaneWave PWLib extracted centroids mapped onto the pooled preview; toggling redraws only the display and preserves stretch/FITS. Solved calibration images carry extracted positions/count in their saved solve result. Older/test images can be inspected once in the isolated native solver without adding calibration points or activating a model; coordinates and preview solve metadata are persisted in SQLite. Detected and catalog-matched star counts are distinct. The calibration legend is vertical on the left and the map is enlarged.

Correction: the checkbox is Solved stars. It draws only catalog-matched centroids from a successful PlaneWave plate solve (PWLib StarStats.SeqNum contains a catalog ID; unmatched extracted stars have -1). Matched centroid count must agree with NumMatchedStars. Extraction-only results cannot enable the overlay; failed solves return an error and no circles. Saved matched coordinates are reused per image.

Camera Preview is a vertically scrolling capture gallery. Single-click selects a thumbnail and its sky position/metadata; double-click opens the large image viewer. Saved stretch settings also update the gallery PNG, so refreshed history uses the selected display stretch. Stretch controls, matched-star overlays, zoom and pan are in the viewer; per-image zoom/pan are persisted with the stretch settings.

New Pointing Model starts a persisted image session: preview, image details and captured-image history show only exposures from that session. Older image records and FITS files remain archived.

Capture thumbnails have chronological frame numbers, per-image Solve RMS and Use in model selection. Excluding an image persists the calibration point inclusion flag used by PointXP fitting and invalidates the previous candidate metrics. Calibration RMS follows the C# sqrt(mean(pointing_error²)) formula over included points; fitted Model RMS uses PointXP RMSErrorRad converted to arcseconds. Point errors compare expected and solved coordinates in the same J2000 frame, with wrapped RA and cos(mean declination).

Automatic calibration excludes a newly solved point when its same-frame pointing error exceeds 100 arcsec. Such points retain their FITS/preview and rejection reason, count as processed in the planned series, and do not contribute to RMS or fitting. Capture & Solve outside the automatic series is unaffected. Operators can explicitly re-include a rejected point with Use in model after review.

## Sky chart Run Demo

Run Demo sits immediately left of Reset view. It sends the IDs currently drawn above the horizon to the controller, which resolves them from its own sky catalog and randomly chooses targets within the existing travel limits. Azimuth and Altitude must already be enabled with fresh telemetry. Demo uses the normal slew and object-rate tracking functions; it waits within 0.25 degrees and below 0.05 degrees/second for one continuous second, tracks for four seconds, then chooses another target. Slews time out after 180 seconds. Manual motion/calibration operations are blocked while Demo runs. Stop Demo, Stop tracking, STOP ALL, disabling an axis, disconnecting the mount, or loss of the browser heartbeat stops the loop and mount motion. The heartbeat expires after ten seconds; closing the page also requests a stop. No demo starts automatically.

Validation: `tests/test_sky_demo.py` covers enable guards, settling, four-second tracking, changing targets, cancellation and heartbeat expiry; `tests/sky-demo-ui.cjs` verifies desktop/mobile placement and Run/Stop with motion requests mocked.


### Windows server clock and satellite display

After a successful NTP test, Next configures the selected host as the persistent
Windows Time manual peer (`0x8` client mode), enables automatic service startup,
and requests resynchronization. The Mount Service process must run as Windows
Administrator for this operation. A failed OS operation keeps the wizard on the
NTP step and does not save the selection as applied. Stop tracking and camera
acquisition before changing the time source.

On service startup and every 60 seconds, the clock monitor checks the saved peer,
Windows source and measured clock offset. `/api/pointing/ntp` exposes the current
`clock` result. Configuration/source discrepancies generate an engineering
warning. With administrator rights, the monitor can restore a previously applied
selection while tracking and acquisition are idle. Windows Time itself remembers
the peer across Windows restarts. NTP measurements do not guarantee a particular
millisecond accuracy over every network.

Map positions now run in browser Web Workers at a target cadence of 100 ms,
independent of server motor tracking. The page loads raw TLE/J2000/orbital-element
catalogs once into temporary page memory. Astronomy and Non-Celestial workers stop
when their workspace or browser tab is hidden, and terminate on page close.
Satellite forward tracks and the three-day horizon forecast also run locally.
No positional WebSocket or recurring positional HTTP polling is used by these maps.

All display workers share a server UTC anchor. Three small clock samples select
the lowest round-trip delay, with midpoint mapping to browser monotonic time.
Clock refresh is every 60 seconds; display propagation continues from the last
anchor during a network outage. Changing pointing GPS/NTP invalidates the anchor.
The context carries the saved latitude, longitude, elevation and DUT1 correction.
This does not improve the server's underlying clock accuracy or old TLE quality.

Vendored display libraries: satellite.js 6.0.1 (Vallado SGP4/SDP4) and Astronomy
Engine 2.1.19, both MIT licensed with license files under web/vendor. Solar-system
map references were compared with the server's Astropy sky-chart ephemeris;
tracking still uses the existing server PWLib path. The legacy PWLib solar result
has a coordinate-frame discrepancy relative to Astropy/Astronomy Engine (up to
about 0.6 degrees azimuth in the fixed-date sample); this migration does not alter
that motor-tracking implementation. Small-body display retains the existing
approximate elliptical Kepler model (e < 1), without perturbations/light-time.

Validation: tests/client-sky-ui.cjs covers offline motion, inactive-tab stop/resume,
local satellite tracks and no positional network stream. The fixed-date reference
fixture in tests/fixtures/client-ephemeris-reference.json compares satellite SGP4,
J2000 catalog coordinates, solar-system Astropy and the server Kepler model via
tests/client-ephemeris-reference.cjs. These are sample comparisons, not a general
precision guarantee for all orbital elements or observing times.

Non-celestial `Track this object` starts the guarded server tracking servo for the selected target. `Demo` submits at most 500 above-horizon chart objects and uses a 10-second browser heartbeat lease. It revalidates server travel and velocity limits before each target and never enables axes. Position RMS uses the last 10 server control samples (arcsec per encoder axis). Demo dwell requires both RMS axes inside the configurable window, resetting on departure: 15 s for TLE mean motion > 6.4 revolutions/day, otherwise 5 s. Acquisition times out at 180 s; Stop tracking, hidden browser/page, connection recovery and shutdown stop the demo. Tests: `test_moving_demo.py` and `moving-demo-ui.cjs` (motion requests mocked).

Astronomy object browser: Calibration catalog retains the existing light display dataset; Extended catalog contains 22,412 BSC5/OpenNGC objects generated from repository snapshots by `scripts/build_extended_sky_catalog.py`. The searchable virtual list follows the Objects filter and searches catalog identifiers, cross-identifiers and common names (NGC/IC/M/HR/HD numeric queries use complete identifiers). Extended mode loads 480×320 DSS2 colour survey cutouts directly in the browser, on selection, using CDS HiPS2FITS; fixed survey images are not presented as current Solar System images. Images need internet access; catalog/controls remain usable when the image service fails. Calibration and pointing-model catalogs remain unchanged. OpenNGC source/attribution: https://github.com/mattiaverga/OpenNGC (CC BY-SA 4.0). Image service: https://alasky.cds.unistra.fr/hips-image-services/hips2fits .
