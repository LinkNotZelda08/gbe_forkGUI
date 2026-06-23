using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GoldbergGUI.Core.Models;
using GoldbergGUI.Core.Utils;
using MvvmCross.Logging;
using NinjaNye.SearchExtensions;
using SQLite;
using SteamStorefrontAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GoldbergGUI.Core.Services
{
    // gets info from steam api
    public interface ISteamService
    {
        public Task Initialize(IMvxLog log);
        public Task<IEnumerable<SteamApp>> GetListOfAppsByName(string name);
        public Task<SteamApp> GetAppByName(string name);
        public Task<SteamApp> GetAppById(int appid);
        public Task<List<Achievement>> GetListOfAchievements(SteamApp steamApp);
        public Task<List<DlcApp>> GetListOfDlc(SteamApp steamApp, bool useSteamDb);
        public Task<WorkshopMod> GetWorkshopModInfo(long workshopId);
        /// <summary>
        /// Downloads and parses the controller VDF file with the given published-file ID.
        /// Use this as the manual-entry fallback.
        /// </summary>
        public Task<List<ControllerActionSet>> GetControllerActionSetsByFileId(long publishedFileId);
        /// <summary>
        /// Comprehensive auto-fetch: scrapes SteamDB for the app's controller config,
        /// downloads and parses any custom VDF, and — for template-only games — returns
        /// the template index and human-readable name so the UI can inform the user.
        /// </summary>
        public Task<(List<ControllerActionSet> ActionSets, int? TemplateIndex, string TemplateName)> GetControllerConfig(int appId);
        /// <summary>Returns the game's stats from the Steam schema API as a list of <see cref="Stat"/>.</summary>
        public Task<List<Stat>> GetStats(int appId);
        /// <summary>Returns a gbe_fork-formatted branches.json string from ISteamApps/GetAppBetas, or null on failure.</summary>
        public Task<string> GetBranchesJson(int appId);
        /// <summary>Returns the Steam language codes supported by the app (for supported_languages.txt), or null on failure.</summary>
        public Task<List<string>> GetSupportedLanguages(int appId);
    }

    class SteamCache
    {
        public string SteamUri { get; }
        public Type ApiVersion { get; }
        public string SteamAppType { get; }

        public SteamCache(string uri, Type apiVersion, string steamAppType)
        {
            SteamUri = uri;
            ApiVersion = apiVersion;
            SteamAppType = steamAppType;
        }
    }

    // ReSharper disable once UnusedType.Global
    // ReSharper disable once ClassNeverInstantiated.Global
    public class SteamService : ISteamService
    {
        // ReSharper disable StringLiteralTypo
        private readonly Dictionary<string, SteamCache> _caches =
            new Dictionary<string, SteamCache>
            {
                {
                    AppTypeGame,
                    new SteamCache(
                        "https://api.steampowered.com/IStoreService/GetAppList/v1/" +
                        "?max_results=50000" +
                        "&include_games=1" +
                        "&key=" + Secrets.SteamWebApiKey(),
                        typeof(SteamAppsV1),
                        AppTypeGame
                    )
                },
                {
                    AppTypeDlc,
                    new SteamCache(
                        "https://api.steampowered.com/IStoreService/GetAppList/v1/" +
                        "?max_results=50000" +
                        "&include_games=0" +
                        "&include_dlc=1" +
                        "&key=" + Secrets.SteamWebApiKey(),
                        typeof(SteamAppsV1),
                        AppTypeDlc
                    )
                }
            };

        private static readonly Secrets Secrets = new Secrets();

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/87.0.4280.88 Safari/537.36";
        private const string AppTypeGame = "game";

        private static readonly HttpClient _httpClient;

        static SteamService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
        private const string AppTypeDlc = "dlc";
        private const string Database = "steamapps.cache";
        private const string GameSchemaUrl = "https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/";

        private IMvxLog _log;

        private SQLiteAsyncConnection _db;

        public async Task Initialize(IMvxLog log)
        {
            static SteamApps DeserializeSteamApps(Type type, string cacheString)
            {
                return type == typeof(SteamAppsV2)
                    ? (SteamApps)JsonSerializer.Deserialize<SteamAppsV2>(cacheString)
                    : JsonSerializer.Deserialize<SteamAppsV1>(cacheString);
            }

            _log = log;
            _db = new SQLiteAsyncConnection(Database);
            await _db.CreateTableAsync<SteamApp>().ConfigureAwait(false);

            var countAsync = await _db.Table<SteamApp>().CountAsync().ConfigureAwait(false);
            if (DateTime.Now.Subtract(File.GetLastWriteTimeUtc(Database)).TotalDays >= 1 || countAsync == 0)
            {
                try
                {
                    foreach (var (appType, steamCache) in _caches)
                    {
                        _log.Info($"Updating cache ({appType})...");
                        bool haveMoreResults;
                        long lastAppId = 0;
                        var cache = new HashSet<SteamApp>();
                        do
                        {
                            var uri = lastAppId > 0
                                ? $"{steamCache.SteamUri}&last_appid={lastAppId}"
                                : steamCache.SteamUri;
                            var response = await _httpClient.GetAsync(uri).ConfigureAwait(false);
                            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var steamApps = DeserializeSteamApps(steamCache.ApiVersion, responseBody);
                            foreach (var app in steamApps.AppList.Apps)
                            {
                                app.AppType = steamCache.SteamAppType;
                                app.ComparableName = PrepareStringToCompare(app.Name);
                                cache.Add(app);
                            }
                            haveMoreResults = steamApps.AppList.HaveMoreResults;
                            lastAppId = steamApps.AppList.LastAppid;
                        } while (haveMoreResults);

                        await _db.InsertAllAsync(cache, "OR IGNORE").ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    _log.Error($"Failed to update Steam app cache (offline?): {e.Message}");
                }
            }
        }

        public async Task<IEnumerable<SteamApp>> GetListOfAppsByName(string name)
        {
            var query = await _db.Table<SteamApp>()
                .Where(x => x.AppType == AppTypeGame).ToListAsync().ConfigureAwait(false);
            var listOfAppsByName = query.Search(x => x.Name)
                .SetCulture(StringComparison.OrdinalIgnoreCase)
                .ContainingAll(name.Split(' '));
            return listOfAppsByName;
        }

        public async Task<SteamApp> GetAppByName(string name)
        {
            _log.Info($"Trying to get app {name}");
            var comparableName = PrepareStringToCompare(name);
            var app = await _db.Table<SteamApp>()
                .FirstOrDefaultAsync(x => x.AppType == AppTypeGame && x.ComparableName.Equals(comparableName))
                .ConfigureAwait(false);
            if (app != null) _log.Info($"Successfully got app {app}");
            return app;
        }

        public async Task<SteamApp> GetAppById(int appid)
        {
            _log.Info($"Trying to get app with ID {appid}");
            var app = await _db.Table<SteamApp>().Where(x => x.AppType == AppTypeGame)
                .FirstOrDefaultAsync(x => x.AppId.Equals(appid)).ConfigureAwait(false);
            if (app != null) _log.Info($"Successfully got app {app}");
            return app;
        }

        public async Task<List<Achievement>> GetListOfAchievements(SteamApp steamApp)
        {
            var achievementList = new List<Achievement>();
            if (steamApp == null)
            {
                return achievementList;
            }

            _log.Info($"Getting achievements for App {steamApp}");

            var apiUrl = $"{GameSchemaUrl}?key={Secrets.SteamWebApiKey()}&appid={steamApp.AppId}&l=en";

            var response = await _httpClient.GetAsync(apiUrl);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var jsonResponse = JsonDocument.Parse(responseBody);
            var achievementData = jsonResponse.RootElement.GetProperty("game")
                .GetProperty("availableGameStats")
                .GetProperty("achievements");

            achievementList = JsonSerializer.Deserialize<List<Achievement>>(achievementData.GetRawText());
            return achievementList;
        }

        public async Task<List<DlcApp>> GetListOfDlc(SteamApp steamApp, bool useSteamDb)
        {
            var dlcList = new List<DlcApp>();
            if (steamApp != null)
            {
                _log.Info($"Get DLC for App {steamApp}");
                var steamAppDetails = await AppDetails.GetAsync(steamApp.AppId).ConfigureAwait(true);
                if (steamAppDetails.Type == AppTypeGame)
                {
                    foreach (var x in steamAppDetails.DLC)
                    {
                        var result = await _db.Table<SteamApp>().Where(z => z.AppType == AppTypeDlc)
                                         .FirstOrDefaultAsync(y => y.AppId.Equals(x)).ConfigureAwait(false)
                                     ?? new SteamApp() { AppId = x, Name = $"Unknown DLC {x}", ComparableName = $"unknownDlc{x}", AppType = AppTypeDlc };
                        dlcList.Add(new DlcApp(result));
                        _log.Debug($"{result.AppId}={result.Name}");
                    }

                    _log.Info("Got DLC successfully...");

                    // Get DLC from SteamDB
                    // Get Cloudflare cookie (not implemented)
                    // Scrape and parse HTML page
                    // Add missing to DLC list

                    // Return current list if we don't intend to use SteamDB
                    if (!useSteamDb) return dlcList;

                    try
                    {
                        var steamDbUri = new Uri($"https://steamdb.info/app/{steamApp.AppId}/dlc/");

                        _log.Info($"Get SteamDB App {steamApp}");
                        var response = await _httpClient.GetAsync(steamDbUri).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();

                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        var parser = new HtmlParser();
                        var doc = parser.ParseDocument(responseBody);

                        var query1 = doc.QuerySelector("#dlc");
                        if (query1 != null)
                        {
                            _log.Info("Got list of DLC from SteamDB.");
                            var query2 = query1.QuerySelectorAll(".app");
                            foreach (var element in query2)
                            {
                                var dlcId = element.GetAttribute("data-appid");
                                var query3 = element.QuerySelectorAll("td");
                                var dlcName = query3 != null
                                    ? query3[1].Text().Replace("\n", "").Trim()
                                    : $"Unknown DLC {dlcId}";
                                var dlcApp = new DlcApp { AppId = Convert.ToInt32(dlcId), Name = dlcName };
                                var i = dlcList.FindIndex(x => x.AppId.Equals(dlcApp.AppId));
                                if (i > -1)
                                {
                                    if (dlcList[i].Name.Contains("Unknown DLC")) dlcList[i] = dlcApp;
                                }
                                else
                                {
                                    dlcList.Add(dlcApp);
                                }
                            }

                            dlcList.ForEach(x => _log.Debug($"{x.AppId}={x.Name}"));
                            _log.Info("Got DLC from SteamDB successfully...");
                        }
                        else
                        {
                            _log.Error("Could not get DLC from SteamDB!");
                        }
                    }
                    catch (Exception e)
                    {
                        _log.Error("Could not get DLC from SteamDB! Skipping...");
                        _log.Error(e.ToString());
                    }
                }
                else
                {
                    _log.Error("Could not get DLC: Steam App is not of type \"game\"");
                }
            }
            else
            {
                _log.Error("Could not get DLC: Invalid Steam App");
            }

            return dlcList;
        }

        public async Task<WorkshopMod> GetWorkshopModInfo(long workshopId)
        {
            _log.Info($"Fetching Steam Workshop info for item {workshopId}...");
            // Including the API key causes the endpoint to return the full response,
            // including the "children" array (required items / dependencies).
            var url = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/" +
                      "?key=" + Secrets.SteamWebApiKey();

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("itemcount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", workshopId.ToString())
            });

            try
            {
                var response = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
                var body     = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _log.Debug($"[Workshop API] Raw response for {workshopId}: {body}");
                var doc      = JsonDocument.Parse(body);

                var details = doc.RootElement
                    .GetProperty("response")
                    .GetProperty("publishedfiledetails")[0];

                var result = details.GetProperty("result").GetInt32();
                if (result != 1)
                {
                    _log.Warn($"Workshop item {workshopId} returned result code {result}.");
                    return new WorkshopMod { WorkshopId = workshopId, Name = $"Unknown Mod ({workshopId})" };
                }

                var title = details.TryGetProperty("title", out var t) ? t.GetString() : null;
                _log.Info($"Got workshop info: {title}");

                return new WorkshopMod
                {
                    WorkshopId = workshopId,
                    Name       = string.IsNullOrWhiteSpace(title) ? $"Unknown Mod ({workshopId})" : title,
                };
            }
            catch (Exception e)
            {
                _log.Error($"Failed to fetch workshop info for {workshopId}: {e.Message}");
                return new WorkshopMod { WorkshopId = workshopId, Name = $"Unknown Mod ({workshopId})" };
            }
        }

        // -----------------------------------------------------------------------
        // Controller VDF auto-fetch
        // -----------------------------------------------------------------------

        // ── Known Steam controller template names (steamcontrollertemplateindex) ─────
        private static readonly Dictionary<int, string> ControllerTemplateNames = new()
        {
            { 0, "Gamepad" },
            { 1, "Gamepad with Camera Controls" },
            { 2, "Gamepad with High Precision Camera/Mouse" },
            { 3, "Keyboard (WASD) and Mouse" },
            { 4, "Gamepad + Keyboard (Mouse)" },
            { 5, "Tablet / Trackpad Controls" },
            { 6, "Minimal Gamepad" },
            { 7, "Dual-Stage Triggers" },
            { 8, "Flight Stick / Joystick" },
        };

        private static string ResolveTemplateName(int index) =>
            ControllerTemplateNames.TryGetValue(index, out var n) ? n : $"Template #{index}";

        /// <summary>
        /// Comprehensive controller config fetch:
        ///   1. Scrapes the SteamDB config page for steamcontrollerconfigdetails (file IDs)
        ///      and steamcontrollertemplateindex.
        ///   2. If file IDs found — downloads + parses the first valid VDF.
        ///   3. If only a template index found — returns it with a human-readable name.
        ///   4. Falls back to IPublishedFileService/QueryFiles if SteamDB is unavailable.
        /// </summary>
        public async Task<(List<ControllerActionSet> ActionSets, int? TemplateIndex, string TemplateName)> GetControllerConfig(int appId)
        {
            _log.Info($"[GetControllerConfig] App {appId}...");

            // ── 1. Local Steam appinfo.vdf (fastest, no network, most reliable) ──────
            var (fileIds, templateIndex) = ReadControllerInfoFromAppInfo(appId);
            _log.Info($"[GetControllerConfig] appinfo.vdf → {fileIds.Count} file ID(s), template={templateIndex?.ToString() ?? "none"}");

            // ── 2. SteamDB scrape (fallback when appinfo.vdf has nothing) ────────────
            if (fileIds.Count == 0 && templateIndex == null)
            {
                _log.Info($"[GetControllerConfig] appinfo.vdf found nothing — trying SteamDB scrape...");
                (fileIds, templateIndex) = await ScrapeControllerConfigFromSteamDb(appId).ConfigureAwait(false);
            }

            var found = await TryGetSetsFromIds(fileIds).ConfigureAwait(false);
            if (found != null) return (found, null, null);

            // If a template index was found (and no custom VDF parsed successfully), return it
            if (templateIndex.HasValue)
            {
                var tname = ResolveTemplateName(templateIndex.Value);
                _log.Info($"[GetControllerConfig] Template {templateIndex}: {tname}");
                return (new List<ControllerActionSet>(), templateIndex, tname);
            }

            // ── 3. QueryFiles API — typed (controller_xbox360/xboxone with KV tag) ───
            _log.Info($"[GetControllerConfig] Trying QueryFiles (typed) for app {appId}...");
            foreach (var ctType in new[] { "controller_xbox360", "controller_xboxone" })
            {
                var qfIds = await QueryControllerFileIds(appId, ctType).ConfigureAwait(false);
                found = await TryGetSetsFromIds(qfIds).ConfigureAwait(false);
                if (found != null) return (found, null, null);
            }

            // ── 4. QueryFiles API — broad (any game-managed file, no tag filter) ─────
            _log.Info($"[GetControllerConfig] Trying QueryFiles (broad) for app {appId}...");
            var broadIds = await QueryControllerFileIdsBroad(appId).ConfigureAwait(false);
            found = await TryGetSetsFromIds(broadIds).ConfigureAwait(false);
            if (found != null) return (found, null, null);

            // ── 5. Steam Store API — detect games with native controller support ──────
            var supportLevel = await GetControllerSupportFromSteamStore(appId).ConfigureAwait(false);
            if (supportLevel is "full" or "partial")
            {
                _log.Info($"[GetControllerConfig] App {appId} reports '{supportLevel}' controller support — treating as native/template.");
                return (new List<ControllerActionSet>(), null, "Native XInput / Generic Gamepad Support");
            }

            _log.Warn($"[GetControllerConfig] No controller config found for app {appId}.");
            return (new List<ControllerActionSet>(), null, null);
        }

        // -----------------------------------------------------------------------
        // Local appinfo.vdf reader
        // -----------------------------------------------------------------------

        /// <summary>
        /// Reads Steam's local <c>appcache\appinfo.vdf</c> to extract
        /// <c>steamcontrollerconfigdetails</c> (file IDs) and
        /// <c>steamcontrollertemplateindex</c> for the given app without any
        /// network access.  Falls back to empty/null if Steam is not found or
        /// the file cannot be parsed.
        /// </summary>
        private (List<long> fileIds, int? templateIndex) ReadControllerInfoFromAppInfo(int appId)
        {
            var empty = (new List<long>(), (int?)null);
            try
            {
                var steamPath = GetSteamInstallPath();
                if (steamPath == null) return empty;

                var appInfoPath = Path.Combine(steamPath, "appcache", "appinfo.vdf");
                if (!File.Exists(appInfoPath)) return empty;

                using var fs  = new FileStream(appInfoPath, FileMode.Open,
                                               FileAccess.Read, FileShare.ReadWrite);
                using var br  = new BinaryReader(fs);

                var magic = br.ReadUInt32();
                br.ReadUInt32();  // universe

                // Supported magic numbers (SteamKit2 convention):
                //   0x07564427 = Magic27 (no size field)
                //   0x07564428 = Magic28 (size field, no extra hash)
                //   0x07564429 = Magic29 (size field + extra binary hash)
                const uint Magic27 = 0x07564427u;
                const uint Magic28 = 0x07564428u;
                const uint Magic29 = 0x07564429u;

                bool hasSizeField  = magic == Magic28 || magic == Magic29;
                bool hasExtraHash  = magic == Magic29;

                if (magic != Magic27 && magic != Magic28 && magic != Magic29)
                {
                    _log.Warn($"[AppInfo] Unknown magic 0x{magic:X8} — skipping.");
                    return empty;
                }

                while (fs.Position < fs.Length - 4)
                {
                    var id = br.ReadUInt32();
                    if (id == 0) break;  // end-of-file sentinel

                    long sectionBase = fs.Position;   // position right after appid
                    uint sectionSize = 0;
                    if (hasSizeField) sectionSize = br.ReadUInt32();

                    if (id != (uint)appId)
                    {
                        // Skip entry
                        if (hasSizeField && sectionSize > 0)
                            fs.Seek(sectionBase + sectionSize, SeekOrigin.Begin);
                        else
                            AppInfoSkipBinaryKv(br);
                        continue;
                    }

                    // ── Found our app ──────────────────────────────────────────
                    // Skip fixed-size per-entry header fields:
                    br.ReadUInt32(); // state
                    br.ReadUInt32(); // lastUpdated
                    br.ReadUInt64(); // accessToken
                    br.ReadBytes(20); // sha1 (text)
                    br.ReadUInt32(); // changeNumber
                    if (hasExtraHash) br.ReadBytes(20); // sha1 (binary)

                    var fileIds = new List<long>();
                    int? templateIdx = null;

                    // Walk the entire KV tree; the keys live somewhere inside.
                    AppInfoWalkKv(br, (key, type, reader) =>
                    {
                        if (key == "steamcontrollerconfigdetails" && type == 0x02)
                        {
                            var val = AppInfoReadCString(reader);
                            foreach (var part in val.Split(
                                         new[] { ' ', '\t', ',', ';' },
                                         StringSplitOptions.RemoveEmptyEntries))
                                if (long.TryParse(part, out var fid) && fid > 0)
                                    fileIds.Add(fid);
                        }
                        else if (key == "steamcontrollertemplateindex" && type == 0x03)
                        {
                            templateIdx = reader.ReadInt32();
                        }
                        else
                        {
                            AppInfoSkipKvValue(reader, type);
                        }
                    });

                    _log.Info($"[AppInfo] App {appId}: {fileIds.Count} VDF ID(s), template={templateIdx?.ToString() ?? "none"}");
                    return (fileIds, templateIdx);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[AppInfo] Error reading appinfo.vdf: {ex.Message}");
            }
            return empty;
        }

        private static string GetSteamInstallPath()
        {
            // Try the standard 32-bit registry key first (most common on 64-bit Windows)
            foreach (var hive in new[] {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
                @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam" })
            {
                var val = Microsoft.Win32.Registry.GetValue(hive, "InstallPath", null)
                       ?? Microsoft.Win32.Registry.GetValue(hive, "SteamPath",   null);
                if (val is string s && Directory.Exists(s)) return s;
            }
            return null;
        }

        // ── Minimal binary-KV helpers ────────────────────────────────────────

        /// <summary>
        /// Walks a binary KV block, invoking <paramref name="onLeaf"/> for every
        /// non-nested entry.  Nested dicts (type 0x01) are recursed automatically.
        /// </summary>
        private static void AppInfoWalkKv(BinaryReader br,
            Action<string, byte, BinaryReader> onLeaf)
        {
            while (true)
            {
                if (br.BaseStream.Position >= br.BaseStream.Length) return;
                byte type = br.ReadByte();
                if (type == 0x00) return;  // end of block

                var key = AppInfoReadCString(br);

                if (type == 0x01)
                {
                    // Nested object — recurse, same visitor
                    AppInfoWalkKv(br, onLeaf);
                }
                else
                {
                    onLeaf(key, type, br);
                }
            }
        }

        /// <summary>Skips an entire binary-KV block (used to skip uninteresting entries).</summary>
        private static void AppInfoSkipBinaryKv(BinaryReader br)
        {
            while (true)
            {
                if (br.BaseStream.Position >= br.BaseStream.Length) return;
                byte type = br.ReadByte();
                if (type == 0x00) return;

                AppInfoReadCString(br); // consume key
                if (type == 0x01)
                    AppInfoSkipBinaryKv(br);  // recurse into nested
                else
                    AppInfoSkipKvValue(br, type);
            }
        }

        /// <summary>Consumes and discards one KV value based on its type byte.</summary>
        private static void AppInfoSkipKvValue(BinaryReader br, byte type)
        {
            switch (type)
            {
                case 0x02: AppInfoReadCString(br); break;   // string
                case 0x03:
                case 0x05: br.ReadInt32(); break;           // int32, pointer
                case 0x04: br.ReadSingle(); break;          // float
                case 0x06: AppInfoReadWString(br); break;   // wstring
                case 0x07: br.ReadUInt32(); break;          // color
                case 0x08:
                case 0x0A: br.ReadUInt64(); break;          // uint64, uint64 alt
                case 0x0B: br.ReadInt64(); break;           // int64
            }
        }

        private static string AppInfoReadCString(BinaryReader br)
        {
            var sb = new System.Text.StringBuilder();
            byte b;
            while ((b = br.ReadByte()) != 0)
                sb.Append((char)b);
            return sb.ToString();
        }

        private static void AppInfoReadWString(BinaryReader br)
        {
            while (true)
            {
                char c = (char)br.ReadUInt16();
                if (c == '\0') break;
            }
        }

        // -----------------------------------------------------------------------
        // Broad QueryFiles fallback (no KV tag filter)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Queries IPublishedFileService for ALL game-managed files (filetype=12)
        /// for the given app, without the controller_type KV-tag filter.
        /// This catches games whose VDFs lack the controller_type tag.
        /// </summary>
        private async Task<List<long>> QueryControllerFileIdsBroad(int appId)
        {
            var ids = new List<long>();
            try
            {
                var key = Uri.EscapeDataString(Secrets.SteamWebApiKey());
                var url = "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/" +
                          $"?key={key}" +
                          $"&query_type=11&page=1&numperpage=20" +
                          $"&appid={appId}" +
                          "&filetype=12" +
                          "&return_details=1";

                var body = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                _log.Debug($"[QueryFiles/broad] {body[..Math.Min(300, body.Length)]}");

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("response", out var resp)) return ids;
                return ParsePublishedFileIdsFromResponse(resp);
            }
            catch (Exception e)
            {
                _log.Warn($"QueryControllerFileIdsBroad: {e.Message}");
            }
            return ids;
        }

        // -----------------------------------------------------------------------

        /// <summary>
        /// Scrapes https://steamdb.info/app/{appId}/config/ and extracts:
        ///   • steamcontrollerconfigdetails  → list of published-file IDs
        ///   • steamcontrollertemplateindex  → integer template index
        /// Returns empty/null values if the page is unavailable (Cloudflare, JS-rendered, etc.).
        /// </summary>
        private async Task<(List<long> fileIds, int? templateIndex)> ScrapeControllerConfigFromSteamDb(int appId)
        {
            var fileIds = new List<long>();
            int? templateIndex = null;
            try
            {
                var url = $"https://steamdb.info/app/{appId}/config/";
                _log.Info($"[SteamDB] Fetching controller config from {url}");

                // Use a realistic browser request to avoid Cloudflare / anti-bot 403.
                // Per-request headers are set on HttpRequestMessage so they don't affect
                // other requests that share _httpClient.
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif," +
                    "image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                req.Headers.TryAddWithoutValidation("Cache-Control", "max-age=0");
                req.Headers.TryAddWithoutValidation("Sec-CH-UA",
                    "\"Google Chrome\";v=\"125\", \"Chromium\";v=\"125\", \"Not/A)Brand\";v=\"24\"");
                req.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile", "?0");
                req.Headers.TryAddWithoutValidation("Sec-CH-UA-Platform", "\"Windows\"");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
                req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");

                var response = await _httpClient.SendAsync(req).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _log.Warn($"[SteamDB] HTTP {(int)response.StatusCode} for app {appId}");
                    return (fileIds, templateIndex);
                }

                var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _log.Debug($"[SteamDB] Response ({html.Length} chars): {html[..Math.Min(300, html.Length)]}");

                // Detect Cloudflare challenge pages (small, contain specific strings)
                if (html.Length < 50_000 &&
                    (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                     html.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
                     html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)))
                {
                    _log.Warn("[SteamDB] Cloudflare challenge page detected — scraping skipped.");
                    return (fileIds, templateIndex);
                }

                var parser = new HtmlParser();
                var doc    = parser.ParseDocument(html);

                // ── Strategy 1: standard <tr>/<td> table rows ───────────────────────
                foreach (var row in doc.QuerySelectorAll("tr"))
                {
                    var cells = row.QuerySelectorAll("td");
                    if (cells.Length < 2) continue;
                    ApplySteamDbKv(
                        cells[0].TextContent.Trim().ToLowerInvariant(),
                        cells[1].TextContent.Trim(),
                        fileIds, ref templateIndex);
                }

                // ── Strategy 2: any element whose entire text IS the key,
                //    followed by the value in the immediately adjacent sibling.
                //    Handles div-based or other non-table layouts.
                if (fileIds.Count == 0 && templateIndex == null)
                {
                    foreach (var elem in doc.All)
                    {
                        var txt = elem.TextContent.Trim().ToLowerInvariant();
                        if (txt != "steamcontrollerconfigdetails" &&
                            txt != "steamcontrollertemplateindex") continue;
                        var sibling = elem.NextElementSibling;
                        if (sibling == null) continue;
                        ApplySteamDbKv(txt, sibling.TextContent.Trim(), fileIds, ref templateIndex);
                    }
                }

                _log.Info($"[SteamDB] Result — file IDs: [{string.Join(", ", fileIds)}], " +
                          $"template: {templateIndex?.ToString() ?? "none"}");
            }
            catch (Exception e)
            {
                _log.Warn($"[SteamDB] Scrape failed for app {appId}: {e.Message}");
            }
            return (fileIds, templateIndex);
        }

        // Helper shared by both SteamDB scraping strategies
        private static void ApplySteamDbKv(string key, string val,
            List<long> fileIds, ref int? templateIndex)
        {
            if (key == "steamcontrollerconfigdetails")
            {
                foreach (var part in val.Split(new[] { ' ', '\t', ',', ';' },
                             StringSplitOptions.RemoveEmptyEntries))
                    if (long.TryParse(part, out var id) && id > 0)
                        fileIds.Add(id);
            }
            else if (key == "steamcontrollertemplateindex")
            {
                if (int.TryParse(val, out var idx))
                    templateIndex = idx;
            }
        }

        /// <summary>
        /// Queries the Steam Store API for the game's controller support level.
        /// Returns "full", "partial", "none", or null on network/parse error.
        /// NOTE: Do NOT add a "filters=" parameter — "controller_support" is not a valid
        /// filter name; sending it causes Steam to return an empty data object.
        /// </summary>
        private async Task<string> GetControllerSupportFromSteamStore(int appId)
        {
            try
            {
                // Full appdetails — no filters, so controller_support is always present
                // when Steam has it set for the game.
                var url  = $"https://store.steampowered.com/api/appdetails/?appids={appId}";
                _log.Info($"[Store API] Checking controller support for app {appId}");
                var body = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                _log.Debug($"[Store API] Response prefix: {body[..Math.Min(200, body.Length)]}");

                using var doc = JsonDocument.Parse(body);

                if (!doc.RootElement.TryGetProperty(appId.ToString(), out var appData))
                {
                    _log.Warn($"[Store API] No entry for appid {appId} in response");
                    return null;
                }

                if (!appData.TryGetProperty("success", out var suc) || !suc.GetBoolean())
                {
                    _log.Warn($"[Store API] success=false for app {appId}");
                    return null;
                }

                if (!appData.TryGetProperty("data", out var data))
                {
                    _log.Warn($"[Store API] No 'data' object for app {appId}");
                    return null;
                }

                // controller_support is absent when the game has no controller support at all
                if (!data.TryGetProperty("controller_support", out var cs))
                {
                    _log.Info($"[Store API] App {appId}: no controller_support field → none");
                    return "none";
                }

                var level = cs.GetString() ?? "none";
                _log.Info($"[Store API] App {appId}: controller_support = {level}");
                return level;
            }
            catch (Exception e)
            {
                _log.Warn($"[Store API] Failed for app {appId}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Downloads and parses the controller VDF published file with the given ID.
        /// This is the manual-entry fallback: users look up the file ID on SteamDB.
        /// </summary>
        public async Task<List<ControllerActionSet>> GetControllerActionSetsByFileId(long publishedFileId)
        {
            try
            {
                var vdfContent = await DownloadPublishedFileContent(publishedFileId).ConfigureAwait(false);
                if (string.IsNullOrEmpty(vdfContent)) return new List<ControllerActionSet>();
                var sets = VdfControllerParser.Parse(vdfContent);
                if (sets.Count > 0)
                    _log.Info($"Parsed {sets.Count} action set(s) from file {publishedFileId}.");
                return sets;
            }
            catch (Exception e)
            {
                _log.Error($"GetControllerActionSetsByFileId({publishedFileId}): {e.Message}");
                return new List<ControllerActionSet>();
            }
        }

        private async Task<List<ControllerActionSet>> TryGetSetsFromIds(IEnumerable<long> ids)
        {
            foreach (var fid in ids)
            {
                var sets = await GetControllerActionSetsByFileId(fid).ConfigureAwait(false);
                if (sets.Count > 0) return sets;
            }
            return null;
        }

        // -----------------------------------------------------------------------
        // Controller VDF helpers
        // -----------------------------------------------------------------------

        private static List<long> ParsePublishedFileIdsFromResponse(JsonElement response)
        {
            var ids = new List<long>();
            if (!response.TryGetProperty("publishedfiledetails", out var arr) &&
                !response.TryGetProperty("publishedfileids", out arr))
                return ids;
            if (arr.ValueKind != JsonValueKind.Array) return ids;

            foreach (var elem in arr.EnumerateArray())
            {
                string idStr = null;
                if (elem.ValueKind == JsonValueKind.Object &&
                    elem.TryGetProperty("publishedfileid", out var fp))
                    idStr = fp.GetString();
                else if (elem.ValueKind == JsonValueKind.String)
                    idStr = elem.GetString();
                if (long.TryParse(idStr, out var id) && id > 0) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// Queries IPublishedFileService/QueryFiles for game-managed files whose
        /// "controller_type" KV tag matches <paramref name="controllerType"/>.
        /// </summary>
        private async Task<List<long>> QueryControllerFileIds(int appId, string controllerType)
        {
            var ids = new List<long>();
            try
            {
                // Array brackets must be percent-encoded; controller_type needs no encoding.
                var key   = Uri.EscapeDataString(Secrets.SteamWebApiKey());
                var ctEnc = Uri.EscapeDataString(controllerType);
                var url = "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/" +
                          $"?key={key}" +
                          $"&query_type=11&page=1&numperpage=20" +
                          $"&appid={appId}" +
                          "&filetype=12" +                          // k_EPublishedFileType_GameManagedItem
                          "&return_details=1" +
                          "&required_kv_tags%5B0%5D%5Bkey%5D=controller_type" +
                          $"&required_kv_tags%5B0%5D%5Bvalue%5D={ctEnc}";

                var body = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                _log.Debug($"[QueryFiles/{controllerType}] {body[..Math.Min(300, body.Length)]}");

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("response", out var resp)) return ids;
                return ParsePublishedFileIdsFromResponse(resp);
            }
            catch (Exception e)
            {
                _log.Warn($"QueryControllerFileIds({controllerType}): {e.Message}");
            }
            return ids;
        }

        /// <summary>
        /// Uses GetPublishedFileDetails to obtain the CDN download URL for a
        /// published file, then downloads its text content.
        /// </summary>
        private async Task<string> DownloadPublishedFileContent(long fileId)
        {
            var apiUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/" +
                         "?key=" + Secrets.SteamWebApiKey();

            var postData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("itemcount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", fileId.ToString())
            });

            var resp = await _httpClient.PostAsync(apiUrl, postData).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            _log.Debug($"[GetPublishedFileDetails/{fileId}] {body[..Math.Min(300, body.Length)]}");

            using var doc = JsonDocument.Parse(body);
            var details = doc.RootElement
                .GetProperty("response")
                .GetProperty("publishedfiledetails")[0];

            if (!details.TryGetProperty("file_url", out var urlProp)) return null;
            var fileUrl = urlProp.GetString();
            if (string.IsNullOrWhiteSpace(fileUrl)) return null;

            _log.Info($"Downloading VDF from {fileUrl}");
            return await _httpClient.GetStringAsync(fileUrl).ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // Stats / Branches / Supported-languages helpers
        // -----------------------------------------------------------------------

        public async Task<List<Stat>> GetStats(int appId)
        {
            var result = new List<Stat>();
            try
            {
                var apiUrl = $"{GameSchemaUrl}?key={Secrets.SteamWebApiKey()}&appid={appId}&l=en";
                var response = await _httpClient.GetAsync(apiUrl).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("game", out var game)) return result;
                if (!game.TryGetProperty("availableGameStats", out var gameStats)) return result;
                if (!gameStats.TryGetProperty("stats", out var statsEl) ||
                    statsEl.ValueKind != JsonValueKind.Array) return result;

                foreach (var statEl in statsEl.EnumerateArray())
                {
                    var stat = new Stat { StatTypeSetting = Stat.StatType.Int };
                    if (statEl.TryGetProperty("name", out var n)) stat.Name = n.GetString();
                    if (statEl.TryGetProperty("defaultvalue", out var dv)) stat.Value = dv.GetRawText();
                    result.Add(stat);
                }
                _log.Info($"GetStats({appId}): {result.Count} stat(s).");
            }
            catch (Exception e)
            {
                _log.Warn($"GetStats({appId}): {e.Message}");
            }
            return result;
        }

        public async Task<string> GetBranchesJson(int appId)
        {
            try
            {
                var url = "https://api.steampowered.com/ISteamApps/GetAppBetas/v1/" +
                          $"?appid={appId}&key={Uri.EscapeDataString(Secrets.SteamWebApiKey())}";
                var body = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                _log.Debug($"[GetBranches] {body[..Math.Min(300, body.Length)]}");

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("response", out var resp)) return null;
                if (!resp.TryGetProperty("betas", out var betas) ||
                    betas.ValueKind != JsonValueKind.Object) return null;

                var branches = new Dictionary<string, object>();
                foreach (var entry in betas.EnumerateObject())
                {
                    var b = entry.Value;
                    branches[entry.Name] = new Dictionary<string, object>
                    {
                        ["build_id"]          = b.TryGetProperty("BuildID",           out var bid) ? bid.GetInt64()     : 0L,
                        ["description"]       = b.TryGetProperty("Description",       out var d)   ? d.GetString() ?? "" : "",
                        ["password_required"] = b.TryGetProperty("PasswordProtected", out var pw)  && pw.GetBoolean(),
                        ["time_updated"]      = b.TryGetProperty("TimeUpdated",       out var tu)  ? tu.GetInt64()     : 0L
                    };
                }

                if (branches.Count == 0) return null;
                _log.Info($"GetBranchesJson({appId}): {branches.Count} branch(es).");
                return JsonSerializer.Serialize(branches, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception e)
            {
                _log.Warn($"GetBranchesJson({appId}): {e.Message}");
                return null;
            }
        }

        public async Task<List<string>> GetSupportedLanguages(int appId)
        {
            try
            {
                var url = $"https://store.steampowered.com/api/appdetails/?appids={appId}";
                var body = await _httpClient.GetStringAsync(url).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty(appId.ToString(), out var appData)) return null;
                if (!appData.TryGetProperty("success", out var suc) || !suc.GetBoolean()) return null;
                if (!appData.TryGetProperty("data", out var data)) return null;
                if (!data.TryGetProperty("supported_languages", out var langsProp)) return null;

                var langsHtml = langsProp.GetString();
                if (string.IsNullOrEmpty(langsHtml)) return null;

                var result = Regex.Replace(langsHtml, "<[^>]+>", "")
                    .Split(',')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Select(MapDisplayNameToSteamCode)
                    .Where(code => code != null)
                    .Distinct()
                    .ToList();

                _log.Info($"GetSupportedLanguages({appId}): {result.Count} language(s).");
                return result.Count > 0 ? result : null;
            }
            catch (Exception e)
            {
                _log.Warn($"GetSupportedLanguages({appId}): {e.Message}");
                return null;
            }
        }

        private static string MapDisplayNameToSteamCode(string displayName) =>
            displayName.ToLowerInvariant() switch
            {
                "english"                 => "english",
                "french"                  => "french",
                "german"                  => "german",
                "spanish - spain"         => "spanish",
                "spanish - latin america" => "latam",
                "simplified chinese"      => "schinese",
                "traditional chinese"     => "tchinese",
                "portuguese - brazil"     => "brazilian",
                "portuguese"              => "portuguese",
                "italian"                 => "italian",
                "dutch"                   => "dutch",
                "polish"                  => "polish",
                "russian"                 => "russian",
                "romanian"                => "romanian",
                "czech"                   => "czech",
                "hungarian"               => "hungarian",
                "danish"                  => "danish",
                "swedish"                 => "swedish",
                "norwegian"               => "norwegian",
                "finnish"                 => "finnish",
                "greek"                   => "greek",
                "turkish"                 => "turkish",
                "ukrainian"               => "ukrainian",
                "japanese"                => "japanese",
                "korean"                  => "koreana",
                "thai"                    => "thai",
                "arabic"                  => "arabic",
                "bulgarian"               => "bulgarian",
                "vietnamese"              => "vietnamese",
                _                         => null
            };

        private static string PrepareStringToCompare(string name)
        {
            return Regex.Replace(name, Misc.AlphaNumOnlyRegex, "").ToLower();
        }
    }
}