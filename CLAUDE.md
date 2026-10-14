# Project Context

## โปรเจค
NARIT Mount Controller API — C# .NET library สำหรับควบคุม Fork Mount กล้องโทรทรรศน์ของ NARIT (สถาบันวิจัยดาราศาสตร์แห่งชาติ)

## Tech Stack
- Language: C# / .NET
- UI Framework: WPF
- Hardware Protocol: PlatinumDrive

## Sub-projects
| โปรเจค | หน้าที่ |
|---|---|
| `NARIT-Mount-API` | Main API library (public interface) |
| `Narit-Mount-Lib` | WPF app + Engine core |
| `NARIT-Mount-API-Tester` | WPF test app |
| `CCD_FLI` | FLI CCD camera module + FITS |
| `CCD_TEST` | CCD test app |

## Main Classes
- `NaritMountController` — entry point, implements `IMountController`
- `AxisController` — ควบคุม Azimuth / Altitude axis
- `SiderealTracking` — tracking ดาว (sidereal rate)
- `SatelliteTracking` — tracking ดาวเทียม
- `PointingModelManager` — pointing correction model
- `CentralizedPollingManager` — polling hardware state

## Data Flow
```
Caller → NaritMountController → AxisController (Az/Alt)
                              → SiderealTracking / SatelliteTracking
                              → PlatinumDrive (hardware layer)
       ←── Events ←── CentralizedPollingManager
```

## Observer Site (NARIT)
- Lat: 18.5736°N, Lon: 98.4820°E, Elevation: 2457m

## วิธีตอบ
- ตอบภาษาไทย
- กระชับ ไม่ต้อง explain ยาว
- ลงมือทำได้เลย ไม่ต้องรอถาม
