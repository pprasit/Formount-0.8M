using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Storage manager for Goto Position presets
    /// Saves and loads position data to/from JSON file
    /// </summary>
    public class GotoPositionStorage
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ForkMount-WPF");

        private static readonly string PositionsFilePath = Path.Combine(
            AppDataFolder,
            "GotoPositions.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
            // Use default property naming (PascalCase) to match C# properties
        };

        /// <summary>
        /// Save goto positions to JSON file
        /// </summary>
        public static bool SavePositions(ObservableCollection<UI.GotoPosition> positions)
        {
            try
            {
                // Ensure directory exists
                Directory.CreateDirectory(AppDataFolder);

                // Convert to list for JSON serialization
                var positionList = new List<PositionData>();
                foreach (var pos in positions)
                {
                    positionList.Add(new PositionData
                    {
                        Id = pos.Id,
                        Azimuth = pos.Azimuth,
                        Altitude = pos.Altitude,
                        Velocity = pos.Velocity
                    });
                }

                // Serialize to JSON
                string json = JsonSerializer.Serialize(positionList, JsonOptions);

                // Write to file
                File.WriteAllText(PositionsFilePath, json);

                SimpleLogger.Info($"[SAVE] Saved {positionList.Count} positions to: {PositionsFilePath}", "Storage");
                return true;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[SAVE] Failed to save positions: {ex.Message}", "Storage");
                return false;
            }
        }

        /// <summary>
        /// Load goto positions from JSON file
        /// </summary>
        public static ObservableCollection<UI.GotoPosition> LoadPositions(ref int nextId)
        {
            var positions = new ObservableCollection<UI.GotoPosition>();

            try
            {
                // Log the file path for debugging
                SimpleLogger.Info($"[LOAD] Attempting to load from: {PositionsFilePath}", "Storage");

                // Check if file exists
                if (!File.Exists(PositionsFilePath))
                {
                    SimpleLogger.Info("[LOAD] No saved positions file found", "Storage");
                    return positions;
                }

                // Read JSON file
                string json = File.ReadAllText(PositionsFilePath);
                SimpleLogger.Info($"[LOAD] File content length: {json.Length} bytes", "Storage");

                if (string.IsNullOrWhiteSpace(json))
                {
                    SimpleLogger.Warning("[LOAD] JSON file is empty", "Storage");
                    return positions;
                }

                // Try to deserialize with PascalCase (current format)
                List<PositionData> positionList = null;
                try
                {
                    positionList = JsonSerializer.Deserialize<List<PositionData>>(json, JsonOptions);
                }
                catch (JsonException)
                {
                    // If PascalCase fails, try camelCase (old format)
                    SimpleLogger.Info("[LOAD] Trying camelCase format...", "Storage");
                    var camelCaseOptions = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };
                    positionList = JsonSerializer.Deserialize<List<PositionData>>(json, camelCaseOptions);
                }

                if (positionList == null || positionList.Count == 0)
                {
                    SimpleLogger.Info("[LOAD] No positions found in file", "Storage");
                    return positions;
                }

                // Convert to ObservableCollection
                int maxId = 0;
                foreach (var data in positionList)
                {
                    positions.Add(new UI.GotoPosition
                    {
                        Id = data.Id,
                        Azimuth = data.Azimuth,
                        Altitude = data.Altitude,
                        Velocity = data.Velocity
                    });

                    if (data.Id > maxId)
                        maxId = data.Id;
                }

                // Update next ID
                nextId = maxId + 1;

                SimpleLogger.Info($"[LOAD] Successfully loaded {positions.Count} positions (Next ID: {nextId})", "Storage");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[LOAD] Failed to load positions: {ex.Message}", "Storage");
                SimpleLogger.Error($"[LOAD] Stack trace: {ex.StackTrace}", "Storage");
            }

            return positions;
        }

        /// <summary>
        /// Delete all saved positions
        /// </summary>
        public static bool ClearPositions()
        {
            try
            {
                if (File.Exists(PositionsFilePath))
                {
                    File.Delete(PositionsFilePath);
                    SimpleLogger.Info("[CLEAR] Deleted saved positions file", "Storage");
                }
                return true;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[CLEAR] Failed to delete positions file: {ex.Message}", "Storage");
                return false;
            }
        }

        /// <summary>
        /// Check if saved positions file exists
        /// </summary>
        public static bool HasSavedPositions()
        {
            return File.Exists(PositionsFilePath);
        }

        /// <summary>
        /// Get the path to saved positions file
        /// </summary>
        public static string GetFilePath()
        {
            return PositionsFilePath;
        }

        /// <summary>
        /// Get file info for debugging
        /// </summary>
        public static string GetFileInfo()
        {
            if (!File.Exists(PositionsFilePath))
            {
                return $"File does not exist: {PositionsFilePath}";
            }

            var fileInfo = new FileInfo(PositionsFilePath);
            return $"File: {PositionsFilePath}\nSize: {fileInfo.Length} bytes\nLast Modified: {fileInfo.LastWriteTime}";
        }

        /// <summary>
        /// Internal data structure for JSON serialization
        /// </summary>
        private class PositionData
        {
            public int Id { get; set; }
            public double Azimuth { get; set; }
            public double Altitude { get; set; }
            public double Velocity { get; set; }
        }
    }
}
