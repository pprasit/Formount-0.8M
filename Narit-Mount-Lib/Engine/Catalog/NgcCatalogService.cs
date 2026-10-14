using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;
using MongoDB.Bson;
using MongoDB.Driver;
using ForkMount_WPF.Engine.Catalog;
using ForkMount_WPF.Engine.Core;
using ForkMount_WPF.Engine.Utility;

namespace ForkMount_WPF.Engine.Catalog
{
    /// <summary>
    /// Service for loading NGC/IC objects from MongoDB
    /// Collection: "NGC" in database "NEOs"
    /// </summary>
    public class NgcCatalogService
    {
        private const string LocalCatalogFileName = "OpenNgcCatalog.csv";
        private readonly IMongoClient _client;
        private readonly IMongoDatabase _database;
        private readonly IMongoCollection<NgcObjectInfo> _ngcCollection;
        private static List<NgcObjectInfo> _localCatalogCache;
        private static readonly object _localCatalogLock = new object();

        private static NgcCatalogService _instance;
        private static readonly object _instanceLock = new object();

        /// <summary>
        /// Get singleton instance
        /// </summary>
        public static NgcCatalogService Instance
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
                                _instance = new NgcCatalogService();
                            }
                            catch (Exception ex)
                            {
                                SimpleLogger.Warning($"[NGC Service] Singleton init failed: {ex.Message}", "NGC");
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
        public NgcCatalogService(string connectionString = "mongodb://localhost:27017", string databaseName = "NEOs")
        {
            try
            {
                _client = new MongoClient(connectionString);
                _database = _client.GetDatabase(databaseName);
                _ngcCollection = _database.GetCollection<NgcObjectInfo>("NGC");

                SimpleLogger.Info($"[NGC Service] Connected to {databaseName}/NGC at {connectionString}", "NGC");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Connection failed: {ex.Message}", "NGC");
                throw;
            }
        }

        /// <summary>
        /// Get total count of NGC objects
        /// </summary>
        public async Task<long> GetTotalCountAsync()
        {
            try
            {
                return await _ngcCollection.CountDocumentsAsync(FilterDefinition<NgcObjectInfo>.Empty);
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Count failed: {ex.Message}", "NGC");
                return 0;
            }
        }

        /// <summary>
        /// Load all NGC objects (may be slow for large catalogs)
        /// </summary>
        public async Task<List<NgcObjectInfo>> LoadAllAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[NGC Service] LoadAllAsync: Starting query...");
                
                var objects = await _ngcCollection.Find(FilterDefinition<NgcObjectInfo>.Empty).ToListAsync();
                
                System.Diagnostics.Debug.WriteLine($"[NGC Service] LoadAllAsync: Query returned {objects.Count} raw objects");
                
                // Parse coordinates for each object
                int parseErrors = 0;
                foreach (var obj in objects)
                {
                    try
                    {
                        obj.ParseCoordinates();
                    }
                    catch
                    {
                        parseErrors++;
                    }
                }
                
                if (parseErrors > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[NGC Service] LoadAllAsync: {parseErrors} coordinate parse errors");
                }

                SimpleLogger.Info($"[NGC Service] Loaded {objects.Count} NGC objects", "NGC");
                return objects;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Load all failed: {ex.Message}", "NGC");
                System.Diagnostics.Debug.WriteLine($"[NGC Service] LoadAllAsync ERROR: {ex.Message}\n{ex.StackTrace}");
                return LoadLocalCatalog();
            }
        }

        /// <summary>
        /// Load NGC objects with pagination
        /// </summary>
        public async Task<List<NgcObjectInfo>> LoadPageAsync(int skip, int limit)
        {
            try
            {
                var objects = await _ngcCollection
                    .Find(FilterDefinition<NgcObjectInfo>.Empty)
                    .Skip(skip)
                    .Limit(limit)
                    .ToListAsync();

                foreach (var obj in objects)
                {
                    obj.ParseCoordinates();
                }

                return objects;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Load page failed: {ex.Message}", "NGC");
                return new List<NgcObjectInfo>();
            }
        }

        /// <summary>
        /// Load Messier objects only (M1-M110)
        /// </summary>
        public async Task<List<NgcObjectInfo>> LoadMessierObjectsAsync()
        {
            try
            {
                // Filter where M field is not empty
                var filter = Builders<NgcObjectInfo>.Filter.And(
                    Builders<NgcObjectInfo>.Filter.Ne("M", ""),
                    Builders<NgcObjectInfo>.Filter.Ne("M", BsonNull.Value)
                );

                var objects = await _ngcCollection.Find(filter).ToListAsync();

                foreach (var obj in objects)
                {
                    obj.ParseCoordinates();
                }

                SimpleLogger.Info($"[NGC Service] Loaded {objects.Count} Messier objects", "NGC");
                return objects.OrderBy(o => int.TryParse(o.Messier, out int m) ? m : 999).ToList();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Load Messier failed: {ex.Message}", "NGC");
                return new List<NgcObjectInfo>();
            }
        }

        /// <summary>
        /// Load objects by type (G=Galaxy, OC=Open Cluster, GC=Globular Cluster, PN=Planetary Nebula, etc.)
        /// </summary>
        public async Task<List<NgcObjectInfo>> LoadByTypeAsync(string type)
        {
            try
            {
                var filter = Builders<NgcObjectInfo>.Filter.Eq("Type", type);
                var objects = await _ngcCollection.Find(filter).ToListAsync();

                foreach (var obj in objects)
                {
                    obj.ParseCoordinates();
                }

                SimpleLogger.Info($"[NGC Service] Loaded {objects.Count} objects of type '{type}'", "NGC");
                return objects;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Load by type failed: {ex.Message}", "NGC");
                return new List<NgcObjectInfo>();
            }
        }

        /// <summary>
        /// Load objects by constellation
        /// </summary>
        public async Task<List<NgcObjectInfo>> LoadByConstellationAsync(string constellation)
        {
            try
            {
                var filter = Builders<NgcObjectInfo>.Filter.Eq("Const", constellation);
                var objects = await _ngcCollection.Find(filter).ToListAsync();

                foreach (var obj in objects)
                {
                    obj.ParseCoordinates();
                }

                return objects;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Load by constellation failed: {ex.Message}", "NGC");
                return new List<NgcObjectInfo>();
            }
        }

        /// <summary>
        /// Search objects by name (NGC/IC number)
        /// </summary>
        public async Task<List<NgcObjectInfo>> SearchByNameAsync(string searchTerm)
        {
            try
            {
                var filter = Builders<NgcObjectInfo>.Filter.Regex("Name", 
                    new BsonRegularExpression(searchTerm, "i"));
                var objects = await _ngcCollection.Find(filter).Limit(100).ToListAsync();

                foreach (var obj in objects)
                {
                    obj.ParseCoordinates();
                }

                return objects;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Search failed: {ex.Message}", "NGC");
                return new List<NgcObjectInfo>();
            }
        }

        /// <summary>
        /// Load objects brighter than specified magnitude
        /// </summary>
        public async Task<List<NgcObjectInfo>> LoadBrighterThanAsync(double maxMagnitude)
        {
            try
            {
                // Load all and filter in memory (MongoDB stores magnitude as string)
                var allObjects = await LoadAllAsync();
                return allObjects
                    .Where(o => o.Magnitude <= maxMagnitude)
                    .OrderBy(o => o.Magnitude)
                    .ToList();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Load brighter than failed: {ex.Message}", "NGC");
                return new List<NgcObjectInfo>();
            }
        }


        /// <summary>
        /// Load popular deep sky objects (Messier + bright NGC)
        /// </summary>
        public async Task<List<NgcObjectInfo>> LoadPopularObjectsAsync(int limit = 500)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[NGC Service] LoadPopularObjectsAsync: Starting...");
                
                // Load all objects directly (simpler approach)
                var allObjects = await LoadAllAsync();
                
                System.Diagnostics.Debug.WriteLine($"[NGC Service] LoadPopularObjectsAsync: Got {allObjects.Count} total objects");
                
                if (allObjects.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[NGC Service] LoadPopularObjectsAsync: No objects returned from LoadAllAsync");
                    return LoadLocalCatalog()
                        .OrderBy(o => o.Magnitude)
                        .Take(limit)
                        .ToList();
                }
                
                // Separate Messier and non-Messier
                var messierObjects = allObjects.Where(o => !string.IsNullOrEmpty(o.Messier)).ToList();
                var ngcObjects = allObjects.Where(o => string.IsNullOrEmpty(o.Messier))
                    .OrderBy(o => o.Magnitude)
                    .Take(limit - messierObjects.Count)
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"[NGC Service] LoadPopularObjectsAsync: Found {messierObjects.Count} Messier + {ngcObjects.Count} NGC");

                // Combine and return
                var result = new List<NgcObjectInfo>();
                result.AddRange(messierObjects);
                result.AddRange(ngcObjects);

                SimpleLogger.Info($"[NGC Service] Loaded {result.Count} popular objects ({messierObjects.Count} Messier + {ngcObjects.Count} NGC)", "NGC");
                return result;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Load popular failed: {ex.Message}", "NGC");
                System.Diagnostics.Debug.WriteLine($"[NGC Service] LoadPopularObjectsAsync ERROR: {ex.Message}\n{ex.StackTrace}");
                return LoadLocalCatalog()
                    .OrderBy(o => o.Magnitude)
                    .Take(limit)
                    .ToList();
            }
        }

        /// <summary>
        /// Get distinct object types in catalog
        /// </summary>
        public async Task<List<string>> GetDistinctTypesAsync()
        {
            try
            {
                var types = await _ngcCollection.DistinctAsync<string>("Type", FilterDefinition<NgcObjectInfo>.Empty);
                return await types.ToListAsync();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Get types failed: {ex.Message}", "NGC");
                return new List<string>();
            }
        }

        /// <summary>
        /// Get distinct constellations in catalog
        /// </summary>
        public async Task<List<string>> GetDistinctConstellationsAsync()
        {
            try
            {
                var constellations = await _ngcCollection.DistinctAsync<string>("Const", FilterDefinition<NgcObjectInfo>.Empty);
                return (await constellations.ToListAsync()).Where(c => !string.IsNullOrEmpty(c)).OrderBy(c => c).ToList();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NGC Service] Get constellations failed: {ex.Message}", "NGC");
                return LoadLocalCatalog()
                    .Select(o => o.Constellation)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();
            }
        }

        public static List<NgcObjectInfo> LoadLocalCatalog()
        {
            if (_localCatalogCache != null)
            {
                return _localCatalogCache;
            }

            lock (_localCatalogLock)
            {
                if (_localCatalogCache != null)
                {
                    return _localCatalogCache;
                }

                string csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", LocalCatalogFileName);
                if (!File.Exists(csvPath))
                {
                    SimpleLogger.Error($"[NGC Service] Local catalog file not found: {csvPath}", "NGC");
                    _localCatalogCache = new List<NgcObjectInfo>();
                    return _localCatalogCache;
                }

                var lines = File.ReadAllLines(csvPath);
                var objects = new List<NgcObjectInfo>();

                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var cols = line.Split(';');
                    if (cols.Length < 24)
                    {
                        continue;
                    }

                    var obj = new NgcObjectInfo
                    {
                        Name = cols[0].Trim(),
                        Type = cols[1].Trim(),
                        RAString = cols[2].Trim(),
                        DecString = cols[3].Trim(),
                        Constellation = cols[4].Trim(),
                        MajAx = cols[5].Trim(),
                        MinAx = cols[6].Trim(),
                        PosAng = cols[7].Trim(),
                        BMag = cols[8].Trim(),
                        VMag = cols[9].Trim(),
                        JMag = cols[10].Trim(),
                        HMag = cols[11].Trim(),
                        KMag = cols[12].Trim(),
                        SurfBr = cols[13].Trim(),
                        Hubble = cols[14].Trim(),
                        Pax = cols[15].Trim(),
                        PmRA = cols[16].Trim(),
                        PmDec = cols[17].Trim(),
                        RadVel = cols[18].Trim(),
                        Redshift = cols[19].Trim(),
                        CstarUMag = cols[20].Trim(),
                        CstarBMag = cols[21].Trim(),
                        CstarVMag = cols[22].Trim(),
                        Messier = cols[23].Trim()
                    };

                    obj.ParseCoordinates();
                    objects.Add(obj);
                }

                _localCatalogCache = objects;
                SimpleLogger.Info($"[NGC Service] Loaded {objects.Count} objects from local OpenNGC catalog", "NGC");
                return _localCatalogCache;
            }
        }
    }
}
