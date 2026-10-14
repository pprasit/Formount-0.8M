using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using ForkMount_WPF.Engine.Core;

namespace ForkMount_WPF.Engine.Utility
{
    /// <summary>
    /// Fetches TLE data from the NARIT TLE REST API.
    /// Endpoint: GET https://engineer.narit.or.th/api/tle.php/content?t={TYPE}
    /// Auth: Bearer token via Authorization header.
    /// </summary>
    public static class NaritTLEService
    {
        private const string CONTENT_URL = "https://engineer.narit.or.th/api/tle.php/content";
        private const string API_TOKEN   = "0x71";

        // Maps internal dropdown type names to NARIT API ?t= parameter values
        private static readonly Dictionary<string, string> TypeToApiParam = new()
        {
            ["NARIT-ACTIVE"]   = "ACTIVE",
            ["NARIT-VISUAL"]   = "VISUAL",
            ["NARIT-STATIONS"] = "STATIONS",
            ["NARIT-STARLINK"] = "STARLINK",
            ["NARIT-GPS-OPS"]  = "GPS-OPS",
            ["NARIT-CUBESAT"]  = "CUBESAT",
        };

        /// <summary>
        /// Returns true if the given satellite type should be fetched from the NARIT API.
        /// </summary>
        public static bool IsNaritType(string satelliteType)
            => TypeToApiParam.ContainsKey(satelliteType);

        /// <summary>
        /// Fetch raw TLE text for the given satellite type from the NARIT API.
        /// Returns the TLE content string, or null on failure.
        /// </summary>
        public static async Task<string> FetchTLEAsync(string satelliteType,
                                                        IProgress<string> progress = null)
        {
            if (!TypeToApiParam.TryGetValue(satelliteType, out string apiParam))
            {
                SimpleLogger.Error($"[NaritTLE] Unknown type: '{satelliteType}'", "NaritTLE");
                return null;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {API_TOKEN}");
            client.DefaultRequestHeaders.Add("User-Agent", "NARIT-MountController/1.0");

            string url = $"{CONTENT_URL}?t={apiParam}";

            progress?.Report($"[NARIT TLE] Fetching {apiParam}...");
            SimpleLogger.Info($"[NaritTLE] Fetching {apiParam} from NARIT API...", "NaritTLE");

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url);
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[NaritTLE] Request failed: {ex.Message}", "NaritTLE");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                SimpleLogger.Error($"[NaritTLE] HTTP {(int)response.StatusCode} for {apiParam}", "NaritTLE");
                return null;
            }

            string content = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                SimpleLogger.Warning($"[NaritTLE] Empty response for {apiParam}", "NaritTLE");
                return null;
            }

            SimpleLogger.Info($"[NaritTLE] Received {content.Length} bytes for {apiParam}", "NaritTLE");
            progress?.Report($"[NARIT TLE] Received {apiParam} ({content.Length / 1024} KB)");
            return content;
        }
    }
}
