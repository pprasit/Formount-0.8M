# MongoDB TLE Integration

## Overview
This document describes the integration between the ForkMount-WPF application and MongoDB for loading satellite Two-Line Element (TLE) data.

## Architecture

### Components
1. **MongoDBService.cs** - Service class for MongoDB connectivity and TLE data loading
2. **SatelliteTrackingControl.xaml.cs** - UI control that displays satellite tracking data
3. **ManualControlWindow.xaml.cs** - Main window that triggers TLE data loading

## Database Schema

### Connection Details
- **Host**: localhost:27017
- **Database**: NEOs
- **Collection**: SatelliteTLE

### Document Structure
```json
{
  "_id": ObjectId("..."),
  "TYPE": "SpaceTrackLEO",
  "TLE": "SATELLITE_NAME\n1 LINE1_DATA\n2 LINE2_DATA",
  "Date": ISODate("2026-01-22T08:51:36.569Z")
}
```

### TLE Field Format
The `TLE` field contains a 3-line format:
```
Line 0: Satellite Name (e.g., "VANGUARD 2")
Line 1: TLE Line 1 (starts with "1 ")
Line 2: TLE Line 2 (starts with "2 ")
```

## Features

### Automatic Loading
- TLE data is automatically loaded from MongoDB when `ManualControlWindow` is opened
- No manual file selection required
- Real-time status updates during loading process

### Data Filtering
- Only loads documents where `TYPE = "SpaceTrackLEO"`
- Validates TLE format before adding to tracker list
- Limits to SCAN_POOL_SIZE (5000) satellites for performance

### Error Handling
- Connection test before attempting to load data
- Graceful fallback if MongoDB is unavailable
- Detailed logging for troubleshooting

## Workflow

### 1. Window Initialization
```
ManualControlWindow Constructor
    ?
InitializeSatelliteTrackingEngine()
    ?
LoadTLEDataFromMongoDB()
```

### 2. Data Loading Process
```
LoadTLEDataFromMongoDB()
    ?
SatelliteTrackingControl.LoadTLEFromMongoDBAsync()
    ?
MongoDBService.LoadTLEDataAsync()
    ?
Parse TLE data ? Initialize SatelliteTrackers
    ?
Start scanning for visible satellites
```

### 3. Status Updates
The status bar shows progress through these stages:
1. `[MongoDB] Connecting to database...`
2. `[MongoDB] Loading TLE data...`
3. `[MongoDB] Initializing X satellites...`
4. `[MongoDB] Initializing... X% (progress)`
5. `[MongoDB] Initialized X satellites. Starting search...`

## Code Examples

### Loading TLE from MongoDB
```csharp
// In ManualControlWindow
private async void LoadTLEDataFromMongoDB()
{
    if (satTrackingControl != null)
    {
        await satTrackingControl.LoadTLEFromMongoDBAsync();
    }
}
```

### MongoDB Service Usage
```csharp
// Initialize service
var mongoService = new MongoDBService("mongodb://localhost:27017", "NEOs");

// Test connection
bool isConnected = await mongoService.TestConnectionAsync();

// Load TLE data
List<SatelliteTLE> tleData = await mongoService.LoadTLEDataAsync();
```

## Configuration

### MongoDB Connection String
Default: `mongodb://localhost:27017`

To change the connection string, modify the initialization in `SatelliteTrackingControl.xaml.cs`:
```csharp
_mongoDBService = new MongoDBService("your-connection-string", "NEOs");
```

### Database Name
Default: `NEOs`

### Collection Name
Default: `SatelliteTLE`

## Troubleshooting

### Common Issues

#### 1. Connection Failed
**Symptoms**: Status shows `[MongoDB] Failed to connect to database`

**Solutions**:
- Verify MongoDB is running: `mongo --eval "db.adminCommand('ping')"`
- Check connection string is correct
- Ensure MongoDB is accessible on localhost:27017

#### 2. No Data Found
**Symptoms**: Status shows `[MongoDB] No TLE data found (TYPE='SpaceTrackLEO')`

**Solutions**:
- Verify documents exist in `SatelliteTLE` collection
- Check `TYPE` field contains exactly `"SpaceTrackLEO"`
- Verify TLE field is properly formatted

#### 3. Invalid TLE Format
**Symptoms**: Satellites load but count is lower than expected

**Solutions**:
- Check TLE field contains 3 lines
- Verify Line 1 starts with "1 "
- Verify Line 2 starts with "2 "
- Check for proper line breaks in TLE field

## Logging

All MongoDB operations are logged with the `[MongoDB]` prefix:
- Connection status
- Number of documents loaded
- Parsing results
- Error messages

Check the application logs for detailed information:
```
SimpleLogger.Info($"[MongoDB] Loaded {tleData.Count} TLE entries", "MongoDB");
```

## Performance

### Loading Time
- Database connection: ~100-500ms
- Data retrieval: ~200-1000ms (depends on document count)
- Satellite initialization: ~3-10s for 1000 satellites

### Memory Usage
- Each satellite tracker: ~5KB
- 1000 satellites: ~5MB
- 5000 satellites: ~25MB

## Migration from File-Based Loading

The original file-based TLE loading is still available but not used by default:
- File: `TLE-Low-Earth.txt`
- Method: `LoadTLEFile(string filePath)`
- Parsing: `ParseTLEFile(string filePath)`

To switch back to file-based loading, comment out the MongoDB call in `ManualControlWindow`:
```csharp
// LoadTLEDataFromMongoDB(); // Comment out to disable MongoDB
```

## Future Enhancements

Potential improvements:
1. **Auto-refresh**: Periodically check for updated TLE data
2. **Multiple sources**: Support different TYPE values (GEO, MEO, etc.)
3. **Caching**: Cache loaded TLE data to reduce database queries
4. **Offline mode**: Fallback to file-based loading if MongoDB unavailable
5. **Configuration**: Add MongoDB settings to AppSettings.json

## References

- MongoDB C# Driver: https://www.mongodb.com/docs/drivers/csharp/
- TLE Format: https://en.wikipedia.org/wiki/Two-line_element_set
- SpaceTrack API: https://www.space-track.org/
