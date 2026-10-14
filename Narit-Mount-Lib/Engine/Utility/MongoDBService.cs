using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Tracking;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Service for connecting to MongoDB and managing application data
    /// Includes: Satellite TLE data, AppSettings
    /// </summary>
    public class MongoDBService
    {
        private readonly IMongoClient _client;
        private readonly IMongoDatabase _database;
        private readonly IMongoCollection<BsonDocument> _satelliteTleCollection;
        private readonly IMongoCollection<BsonDocument> _appSettingsCollection;
        
        // Singleton instance for AppSettings operations
        private static MongoDBService _instance;
        private static readonly object _instanceLock = new object();
        
        /// <summary>
        /// Settings document ID (fixed ID for single settings document)
        /// </summary>
        private const string SETTINGS_DOCUMENT_ID = "ForkMountSettings";

        /// <summary>
        /// Get singleton instance for AppSettings operations
        /// </summary>
        public static MongoDBService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            try
                            {
                                _instance = new MongoDBService();
                            }
                            catch (Exception ex)
                            {
                                SimpleLogger.Warning($"[MongoDB] Singleton init failed: {ex.Message}", "MongoDB");
                                return null;
                            }
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Initialize MongoDB connection
        /// </summary>
        /// <param name="connectionString">MongoDB connection string (default: localhost:27017)</param>
        /// <param name="databaseName">Database name (default: NEOs)</param>
        public MongoDBService(string connectionString = "mongodb://localhost:27017", string databaseName = "NEOs")
        {
            try
            {
                _client = new MongoClient(connectionString);
                _database = _client.GetDatabase(databaseName);
                _satelliteTleCollection = _database.GetCollection<BsonDocument>("SatelliteTLE");
                _appSettingsCollection = _database.GetCollection<BsonDocument>("AppSettings");
                
                SimpleLogger.Info($"[MongoDB] Connected to {databaseName} at {connectionString}", "MongoDB");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Connection failed: {ex.Message}", "MongoDB");
                throw;
            }
        }
        
        #region AppSettings Operations
        
        /// <summary>
        /// Legacy settings loader retained for backward compatibility.
        /// New code should use AppSettings and AppSetting.json instead.
        /// </summary>
        /// <returns>AppSettingsModel or null if not found</returns>
        [Obsolete("Settings are now stored in AppSetting.json via AppSettings. Avoid loading AppSettings from MongoDB.")]
        public AppSettingsModel LoadAppSettings()
        {
            try
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", SETTINGS_DOCUMENT_ID);
                var document = _appSettingsCollection.Find(filter).FirstOrDefault();
                
                if (document == null)
                {
                    SimpleLogger.Info("[MongoDB] No settings found in database - returning null", "MongoDB");
                    return null;
                }
                
                // Remove MongoDB _id field before deserialization
                document.Remove("_id");
                
                // Deserialize to AppSettingsModel
                var json = document.ToJson();
                var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettingsModel>(json);
                
                SimpleLogger.Info($"[MongoDB] Loaded AppSettings from database (LastModified: {settings?.LastModified})", "MongoDB");
                return settings;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Failed to load AppSettings: {ex.Message}", "MongoDB");
                return null;
            }
        }
        
        /// <summary>
        /// Legacy settings loader retained for backward compatibility.
        /// New code should use AppSettings and AppSetting.json instead.
        /// </summary>
        [Obsolete("Settings are now stored in AppSetting.json via AppSettings. Avoid loading AppSettings from MongoDB.")]
        public async Task<AppSettingsModel> LoadAppSettingsAsync()
        {
            try
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", SETTINGS_DOCUMENT_ID);
                var document = await _appSettingsCollection.Find(filter).FirstOrDefaultAsync();
                
                if (document == null)
                {
                    SimpleLogger.Info("[MongoDB] No settings found in database - returning null", "MongoDB");
                    return null;
                }
                
                // Remove MongoDB _id field before deserialization
                document.Remove("_id");
                
                // Deserialize to AppSettingsModel
                var json = document.ToJson();
                var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettingsModel>(json);
                
                SimpleLogger.Info($"[MongoDB] Loaded AppSettings from database (LastModified: {settings?.LastModified})", "MongoDB");
                return settings;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Failed to load AppSettings async: {ex.Message}", "MongoDB");
                return null;
            }
        }
        
        /// <summary>
        /// Legacy settings saver retained for backward compatibility.
        /// New code should use AppSettings and AppSetting.json instead.
        /// </summary>
        /// <param name="settings">Settings to save</param>
        /// <returns>True if successful</returns>
        [Obsolete("Settings are now stored in AppSetting.json via AppSettings. Avoid saving AppSettings to MongoDB.")]
        public bool SaveAppSettings(AppSettingsModel settings)
        {
            try
            {
                if (settings == null)
                {
                    SimpleLogger.Warning("[MongoDB] Cannot save null settings", "MongoDB");
                    return false;
                }
                
                settings.LastModified = DateTime.Now;
                
                // Serialize to JSON then to BsonDocument
                var json = System.Text.Json.JsonSerializer.Serialize(settings);
                var document = BsonDocument.Parse(json);
                
                // Add fixed _id for upsert
                document["_id"] = SETTINGS_DOCUMENT_ID;
                
                // Upsert (insert or replace)
                var filter = Builders<BsonDocument>.Filter.Eq("_id", SETTINGS_DOCUMENT_ID);
                var options = new ReplaceOptions { IsUpsert = true };
                _appSettingsCollection.ReplaceOne(filter, document, options);
                
                SimpleLogger.Info($"[MongoDB] Saved AppSettings to database (LastModified: {settings.LastModified})", "MongoDB");
                return true;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Failed to save AppSettings: {ex.Message}", "MongoDB");
                return false;
            }
        }
        
        /// <summary>
        /// Legacy settings saver retained for backward compatibility.
        /// New code should use AppSettings and AppSetting.json instead.
        /// </summary>
        [Obsolete("Settings are now stored in AppSetting.json via AppSettings. Avoid saving AppSettings to MongoDB.")]
        public async Task<bool> SaveAppSettingsAsync(AppSettingsModel settings)
        {
            try
            {
                if (settings == null)
                {
                    SimpleLogger.Warning("[MongoDB] Cannot save null settings", "MongoDB");
                    return false;
                }
                
                settings.LastModified = DateTime.Now;
                
                // Serialize to JSON then to BsonDocument
                var json = System.Text.Json.JsonSerializer.Serialize(settings);
                var document = BsonDocument.Parse(json);
                
                // Add fixed _id for upsert
                document["_id"] = SETTINGS_DOCUMENT_ID;
                
                // Upsert (insert or replace)
                var filter = Builders<BsonDocument>.Filter.Eq("_id", SETTINGS_DOCUMENT_ID);
                var options = new ReplaceOptions { IsUpsert = true };
                await _appSettingsCollection.ReplaceOneAsync(filter, document, options);
                
                SimpleLogger.Info($"[MongoDB] Saved AppSettings to database async (LastModified: {settings.LastModified})", "MongoDB");
                return true;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Failed to save AppSettings async: {ex.Message}", "MongoDB");
                return false;
            }
        }
        
        /// <summary>
        /// Legacy helper for older MongoDB-backed settings deployments.
        /// </summary>
        [Obsolete("Settings are now stored in AppSetting.json via AppSettings.")]
        public bool HasAppSettings()
        {
            try
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", SETTINGS_DOCUMENT_ID);
                var count = _appSettingsCollection.CountDocuments(filter);
                return count > 0;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Failed to check AppSettings existence: {ex.Message}", "MongoDB");
                return false;
            }
        }
        
        /// <summary>
        /// Legacy helper for older MongoDB-backed settings deployments.
        /// </summary>
        [Obsolete("Settings are now stored in AppSetting.json via AppSettings.")]
        public bool DeleteAppSettings()
        {
            try
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", SETTINGS_DOCUMENT_ID);
                var result = _appSettingsCollection.DeleteOne(filter);
                
                if (result.DeletedCount > 0)
                {
                    SimpleLogger.Info("[MongoDB] Deleted AppSettings from database", "MongoDB");
                    return true;
                }
                else
                {
                    SimpleLogger.Warning("[MongoDB] No AppSettings found to delete", "MongoDB");
                    return false;
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Failed to delete AppSettings: {ex.Message}", "MongoDB");
                return false;
            }
        }
        
        #endregion
        
        #region Satellite TLE Operations

        /// <summary>
        /// Load all TLE data from MongoDB where TYPE = "SpaceTrackLEO"
        /// </summary>
        /// <returns>List of SatelliteTLE objects</returns>
        public async Task<List<SatelliteTLE>> LoadTLEDataAsync()
        {
            return await LoadTLEDataByTypeAsync("SpaceTrackLEO");
        }
        
        /// <summary>
        /// Load TLE data from MongoDB by specified TYPE
        /// </summary>
        /// <param name="satelliteType">TYPE value to filter (e.g., "SpaceTrackLEO", "Geostationary", "StarLink", etc.)</param>
        /// <returns>List of SatelliteTLE objects</returns>
        public async Task<List<SatelliteTLE>> LoadTLEDataByTypeAsync(string satelliteType)
        {
            var tleList = new List<SatelliteTLE>();
            
            try
            {
                SimpleLogger.Info($"[MongoDB] Loading TLE data (TYPE = '{satelliteType}')...", "MongoDB");

                // Filter by TYPE
                var filter = Builders<BsonDocument>.Filter.Eq("TYPE", satelliteType);
                var documents = await _satelliteTleCollection.Find(filter).ToListAsync();

                SimpleLogger.Info($"[MongoDB] Found {documents.Count} documents for TYPE='{satelliteType}'", "MongoDB");

                int parseSuccessCount = 0;
                int parseFailCount = 0;

                foreach (var doc in documents)
                {
                    try
                    {
                        // TLE field contains the full 3-line TLE format for MULTIPLE satellites:
                        // 0 VANGUARD 2
                        // 1 11U 59001A   26021.39668926...
                        // 2 11  32.8763 280.6072...
                        // 0 VANGUARD R/B
                        // 1 12U 59001B   26021.38478668...
                        // 2 12  32.9007  22.6144...
                        // ... (thousands of satellites)
                        
                        string tleText = doc["TLE"].AsString;
                        
                        // Split by various newline combinations
                        var lines = tleText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(line => line.Trim())
                                          .Where(line => !string.IsNullOrWhiteSpace(line))
                                          .ToArray();

                        SimpleLogger.Info($"[MongoDB] Processing TLE string with {lines.Length} lines (estimated {lines.Length / 3} satellites)", "MongoDB");

                        // Process TLE data in groups of 3 lines
                        for (int i = 0; i < lines.Length - 2; i += 3)
                        {
                            try
                            {
                                // [FIX] Remove "0 " prefix from Line 0 if present
                                string name = lines[i];
                                if (name.StartsWith("0 "))
                                {
                                    name = name.Substring(2).Trim();
                                }

                                string line1 = lines[i + 1];
                                string line2 = lines[i + 2];

                                // Validate TLE format
                                if (line1.StartsWith("1 ") && line2.StartsWith("2 "))
                                {
                                    tleList.Add(new SatelliteTLE(name, line1, line2));
                                    parseSuccessCount++;
                                }
                                else
                                {
                                    parseFailCount++;
                                    if (parseFailCount <= 3)
                                    {
                                        SimpleLogger.Warning($"[MongoDB] Invalid TLE format: {name} (Line1: '{line1.Substring(0, Math.Min(5, line1.Length))}', Line2: '{line2.Substring(0, Math.Min(5, line2.Length))}')", "MongoDB");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                parseFailCount++;
                                if (parseFailCount <= 3)
                                {
                                    SimpleLogger.Warning($"[MongoDB] Failed to parse TLE at line {i}: {ex.Message}", "MongoDB");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        parseFailCount++;
                        if (parseFailCount <= 3)
                        {
                            SimpleLogger.Warning($"[MongoDB] Failed to parse document: {ex.Message}", "MongoDB");
                        }
                    }
                }

                SimpleLogger.Info($"[MongoDB] Parse results: Success={parseSuccessCount}, Failed={parseFailCount}", "MongoDB");
                SimpleLogger.Info($"[MongoDB] Successfully parsed {tleList.Count} TLE entries for TYPE='{satelliteType}'", "MongoDB");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Failed to load TLE data: {ex.Message}", "MongoDB");
                throw;
            }

            return tleList;
        }

        /// <summary>
        /// Save (upsert) bulk TLE content for a given satellite type
        /// </summary>
        public async Task<bool> SaveTLEDataAsync(string satelliteType, string tleContent)
        {
            try
            {
                var filter   = Builders<BsonDocument>.Filter.Eq("TYPE", satelliteType);
                var document = new BsonDocument
                {
                    { "TYPE",      satelliteType },
                    { "TLE",       tleContent    },
                    { "UpdatedAt", DateTime.UtcNow }
                };
                var options = new ReplaceOptions { IsUpsert = true };
                await _satelliteTleCollection.ReplaceOneAsync(filter, document, options);
                SimpleLogger.Info($"[MongoDB] Saved TLE data for TYPE='{satelliteType}' ({tleContent.Length} chars)", "MongoDB");
                return true;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Failed to save TLE data: {ex.Message}", "MongoDB");
                return false;
            }
        }

        /// <summary>
        /// Test MongoDB connection
        /// </summary>
        /// <returns>True if connection is successful</returns>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                await _database.RunCommandAsync((Command<BsonDocument>)"{ping:1}");
                SimpleLogger.Info("[MongoDB] Connection test successful", "MongoDB");
                return true;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[MongoDB] Connection test failed: {ex.Message}", "MongoDB");
                return false;
            }
        }
        
        #endregion
    }
}
