using GoldbergGUI.Core.Models;
using GoldbergGUI.Core.Utils;
using MvvmCross.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace GoldbergGUI.Core.Services
{
    // Downloads and updates gbe_fork, sets up config files in gbe_fork INI format,
    // and manages DLL copy/restore operations.
    public interface IGoldbergService
    {
        Task<GoldbergGlobalConfiguration> Initialize(IMvxLog log);
        Task<GoldbergConfiguration> Read(string path);
        Task Save(string path, GoldbergConfiguration configuration);
        Task<GoldbergGlobalConfiguration> GetGlobalSettings();
        Task SetGlobalSettings(GoldbergGlobalConfiguration configuration);
        bool GoldbergApplied(string path);
        Task<bool> Revert(string path);
        Task GenerateInterfacesFile(string filePath);
        List<string> Languages();
    }

    // ReSharper disable once UnusedType.Global
    // ReSharper disable once ClassNeverInstantiated.Global
    public class GoldbergService : IGoldbergService
    {
        // -----------------------------------------------------------------------
        // Constants & paths
        // -----------------------------------------------------------------------
        private const string DefaultAccountName = "Mr_Goldberg";
        private const long DefaultSteamId = 76561197960287930;
        private const string DefaultLanguage = "english";
        private const string GbeReleaseApiUrl = "https://api.github.com/repos/Detanup01/gbe_fork/releases/latest";

        // Valid Steam64 ID range
        private const long SteamIdMin = 76561197960265729;
        private const long SteamIdMax = 76561202255233023;

        private readonly string _goldbergZipPath = Path.Combine(Directory.GetCurrentDirectory(), "goldberg.zip");
        private readonly string _goldbergPath    = Path.Combine(Directory.GetCurrentDirectory(), "goldberg");

        // gbe_fork uses "GSE Saves" instead of "Goldberg SteamEmu Saves"
        private static readonly string GlobalSettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves");

        private readonly string _globalUserIniPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GSE Saves", "settings", "configs.user.ini");

        // ReSharper disable StringLiteralTypo
        private readonly List<string> _interfaceNames = new List<string>
        {
            "SteamClient", "SteamGameServer", "SteamGameServerStats", "SteamUser",
            "SteamFriends", "SteamUtils", "SteamMatchMaking", "SteamMatchMakingServers",
            "STEAMUSERSTATS_INTERFACE_VERSION", "STEAMAPPS_INTERFACE_VERSION",
            "SteamNetworking", "STEAMREMOTESTORAGE_INTERFACE_VERSION",
            "STEAMSCREENSHOTS_INTERFACE_VERSION", "STEAMHTTP_INTERFACE_VERSION",
            "STEAMUNIFIEDMESSAGES_INTERFACE_VERSION", "STEAMUGC_INTERFACE_VERSION",
            "STEAMAPPLIST_INTERFACE_VERSION", "STEAMMUSIC_INTERFACE_VERSION",
            "STEAMMUSICREMOTE_INTERFACE_VERSION", "STEAMHTMLSURFACE_INTERFACE_VERSION_",
            "STEAMINVENTORY_INTERFACE_V", "SteamController", "SteamMasterServerUpdater",
            "STEAMVIDEO_INTERFACE_V"
        };
        // ReSharper restore StringLiteralTypo

        private IMvxLog _log;

        // -----------------------------------------------------------------------
        // Initialise: download/extract latest gbe_fork, then load global settings
        // -----------------------------------------------------------------------
        public async Task<GoldbergGlobalConfiguration> Initialize(IMvxLog log)
        {
            _log = log;
            var downloadedTag = await Download().ConfigureAwait(false);
            if (downloadedTag != null)
            {
                await Extract(_goldbergZipPath).ConfigureAwait(false);
                // Only record the tag after a verified extraction
                var tagPath = Path.Combine(_goldbergPath, "release_tag");
                var x86Dll  = Path.Combine(_goldbergPath, "steam_api.dll");
                var x64Dll  = Path.Combine(_goldbergPath, "steam_api64.dll");
                if (File.Exists(x86Dll) || File.Exists(x64Dll))
                    await File.WriteAllTextAsync(tagPath, downloadedTag).ConfigureAwait(false);
            }
            return await GetGlobalSettings().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // Global settings — read from GSE Saves/settings/configs.user.ini
        // -----------------------------------------------------------------------
        public async Task<GoldbergGlobalConfiguration> GetGlobalSettings()
        {
            _log.Info("Getting global settings...");
            EnsureDirectory(Path.GetDirectoryName(_globalUserIniPath));

            var accountName      = DefaultAccountName;
            var steamId          = DefaultSteamId;
            var language         = DefaultLanguage;
            var customBroadcastIps = new List<string>();

            if (File.Exists(_globalUserIniPath))
            {
                await Task.Run(() =>
                {
                    var ini = ReadIniFile(_globalUserIniPath);
                    if (ini.TryGetValue("user::general", out var general))
                    {
                        if (general.TryGetValue("account_name", out var name) && !string.IsNullOrWhiteSpace(name))
                            accountName = name.Trim();

                        if (general.TryGetValue("account_steamid", out var sid) &&
                            long.TryParse(sid.Trim(), out var parsedId) &&
                            IsValidSteamId(parsedId))
                            steamId = parsedId;

                        if (general.TryGetValue("language", out var lang) && !string.IsNullOrWhiteSpace(lang))
                            language = lang.Trim();
                    }

                    if (ini.TryGetValue("user::saves", out var saves) &&
                        saves.TryGetValue("custom_broadcasts", out var broadcasts) &&
                        !string.IsNullOrWhiteSpace(broadcasts))
                    {
                        customBroadcastIps.AddRange(
                            broadcasts.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                    }
                }).ConfigureAwait(false);
            }

            _log.Info("Got global settings.");
            return new GoldbergGlobalConfiguration
            {
                AccountName       = accountName,
                UserSteamId       = steamId,
                Language          = language,
                CustomBroadcastIps = customBroadcastIps
            };
        }

        // -----------------------------------------------------------------------
        // Global settings — write to GSE Saves/settings/configs.user.ini
        // -----------------------------------------------------------------------
        public async Task SetGlobalSettings(GoldbergGlobalConfiguration c)
        {
            _log.Info("Setting global settings...");
            EnsureDirectory(Path.GetDirectoryName(_globalUserIniPath));

            var accountName = string.IsNullOrEmpty(c.AccountName) ? DefaultAccountName : c.AccountName;
            var userSteamId = IsValidSteamId(c.UserSteamId) ? c.UserSteamId : DefaultSteamId;
            var language    = string.IsNullOrEmpty(c.Language) ? DefaultLanguage : c.Language;

            var sb = new StringBuilder();
            sb.AppendLine("[user::general]");
            sb.AppendLine($"account_name={accountName}");
            sb.AppendLine($"account_steamid={userSteamId}");
            sb.AppendLine($"language={language}");
            sb.AppendLine();

            if (c.CustomBroadcastIps?.Count > 0)
            {
                sb.AppendLine("[user::saves]");
                sb.AppendLine($"custom_broadcasts={string.Join(",", c.CustomBroadcastIps)}");
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(_globalUserIniPath, sb.ToString()).ConfigureAwait(false);
            _log.Info("Setting global configuration finished.");
        }

        // -----------------------------------------------------------------------
        // Read per-game config from steam_settings/ (gbe_fork INI format)
        // -----------------------------------------------------------------------
        public async Task<GoldbergConfiguration> Read(string path)
        {
            _log.Info("Reading configuration...");
            var appId          = -1;
            var achievementList = new List<Achievement>();
            var dlcList        = new List<DlcApp>();

            // AppID: prefer steam_settings/steam_appid.txt, fall back to root
            var appIdFile = FirstExisting(
                Path.Combine(path, "steam_settings", "steam_appid.txt"),
                Path.Combine(path, "steam_appid.txt"));

            if (appIdFile != null)
            {
                _log.Info("Getting AppID...");
                await Task.Run(() => int.TryParse(File.ReadLines(appIdFile).First().Trim(), out appId))
                    .ConfigureAwait(false);
            }
            else
            {
                _log.Info("steam_appid.txt missing! Skipping...");
            }

            // Achievements
            var achievementJson = Path.Combine(path, "steam_settings", "achievements.json");
            if (File.Exists(achievementJson))
            {
                _log.Info("Getting achievements...");
                var json = await File.ReadAllTextAsync(achievementJson).ConfigureAwait(false);
                achievementList = System.Text.Json.JsonSerializer.Deserialize<List<Achievement>>(json);

                if (appId > 0)
                {
                    var userAchievementsPath = Path.Combine(GlobalSettingsPath, appId.ToString(), "achievements.json");
                    if (File.Exists(userAchievementsPath))
                    {
                        _log.Info("Reading unlocked achievements from GSE Saves...");
                        var userJson = await File.ReadAllTextAsync(userAchievementsPath).ConfigureAwait(false);
                        var userDoc  = System.Text.Json.JsonDocument.Parse(userJson);
                        foreach (var achievement in achievementList)
                        {
                            if (userDoc.RootElement.TryGetProperty(achievement.Name, out var achEl) &&
                                achEl.TryGetProperty("earned", out var earned))
                                achievement.Unlocked = earned.GetBoolean();
                        }
                    }
                }
            }
            else
            {
                _log.Info("\"steam_settings/achievements.json\" missing! Skipping...");
            }

            // DLC: prefer configs.app.ini, fall back to legacy DLC.txt
            var configsAppIni = Path.Combine(path, "steam_settings", "configs.app.ini");
            var dlcTxtLegacy  = Path.Combine(path, "steam_settings", "DLC.txt");
            var appPathTxt    = Path.Combine(path, "steam_settings", "app_paths.txt");

            if (File.Exists(configsAppIni))
            {
                _log.Info("Getting DLCs from configs.app.ini...");
                await Task.Run(() =>
                {
                    var ini = ReadIniFile(configsAppIni);
                    if (ini.TryGetValue("app::dlcs", out var dlcs))
                        foreach (var kv in dlcs)
                            if (int.TryParse(kv.Key, out var id))
                                dlcList.Add(new DlcApp { AppId = id, Name = kv.Value, Enabled = true });

                    if (ini.TryGetValue("app::dlcs_disabled", out var disabled))
                        foreach (var kv in disabled)
                            if (int.TryParse(kv.Key, out var id))
                                dlcList.Add(new DlcApp { AppId = id, Name = kv.Value, Enabled = false });

                    if (ini.TryGetValue("app::paths", out var paths))
                        foreach (var kv in paths)
                            if (int.TryParse(kv.Key, out var pid))
                            {
                                var i = dlcList.FindIndex(x => x.AppId == pid);
                                if (i >= 0) dlcList[i].AppPath = kv.Value;
                            }
                }).ConfigureAwait(false);
            }
            else if (File.Exists(dlcTxtLegacy))
            {
                _log.Info("Getting DLCs from legacy DLC.txt...");
                var kvRegex  = new Regex(@"(?<id>.*) *= *(?<n>.*)");
                var lines    = await File.ReadAllLinesAsync(dlcTxtLegacy).ConfigureAwait(false);
                foreach (var line in lines)
                {
                    var m = kvRegex.Match(line);
                    if (m.Success)
                        dlcList.Add(new DlcApp
                        {
                            AppId   = Convert.ToInt32(m.Groups["id"].Value),
                            Name    = m.Groups["n"].Value,
                            Enabled = true
                        });
                }

                if (File.Exists(appPathTxt))
                {
                    var pathRegex    = new Regex(@"(?<id>.*) *= *(?<appPath>.*)");
                    var appPathLines = await File.ReadAllLinesAsync(appPathTxt).ConfigureAwait(false);
                    foreach (var line in appPathLines)
                    {
                        var m = pathRegex.Match(line);
                        if (!m.Success) continue;
                        var i = dlcList.FindIndex(x => x.AppId == Convert.ToInt32(m.Groups["id"].Value));
                        if (i >= 0) dlcList[i].AppPath = m.Groups["appPath"].Value;
                    }
                }
            }
            else
            {
                _log.Info("No DLC config found! Skipping...");
            }

            // Connectivity flags: prefer configs.main.ini, fall back to legacy flag files
            var offline           = false;
            var disableNetworking = false;
            var disableOverlay    = false;
            var configsMainIni    = Path.Combine(path, "steam_settings", "configs.main.ini");

            if (File.Exists(configsMainIni))
            {
                await Task.Run(() =>
                {
                    var ini = ReadIniFile(configsMainIni);
                    if (ini.TryGetValue("main::connectivity", out var conn))
                    {
                        offline           = IniFlag(conn, "offline");
                        disableNetworking = IniFlag(conn, "disable_networking");
                        disableOverlay    = IniFlag(conn, "disable_overlay");
                    }
                }).ConfigureAwait(false);
            }
            else
            {
                // Legacy flag files from original Goldberg — kept for backwards compatibility
                offline           = File.Exists(Path.Combine(path, "steam_settings", "offline.txt"));
                disableNetworking = File.Exists(Path.Combine(path, "steam_settings", "disable_networking.txt"));
                disableOverlay    = File.Exists(Path.Combine(path, "steam_settings", "disable_overlay.txt"));
            }

            return new GoldbergConfiguration
            {
                AppId             = appId,
                Achievements      = achievementList,
                DlcList           = dlcList,
                Offline           = offline,
                DisableNetworking = disableNetworking,
                DisableOverlay    = disableOverlay
            };
        }

        // -----------------------------------------------------------------------
        // Save per-game config in gbe_fork INI format
        // -----------------------------------------------------------------------
        public async Task Save(string path, GoldbergConfiguration c)
        {
            _log.Info("Saving configuration...");

            // DLL setup — copy Goldberg DLL into place
            _log.Info("Running DLL setup...");
            foreach (var dllName in new[] { "steam_api", "steam_api64" })
                if (File.Exists(Path.Combine(path, $"{dllName}.dll")))
                    CopyDllFiles(path, dllName);
            _log.Info("DLL setup finished!");

            var settingsDir = Path.Combine(path, "steam_settings");
            EnsureDirectory(settingsDir);

            // steam_appid.txt: write in steam_settings/ (gbe_fork) AND beside the DLL (compat)
            var appIdText = c.AppId.ToString();
            await File.WriteAllTextAsync(Path.Combine(settingsDir, "steam_appid.txt"), appIdText).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(path, "steam_appid.txt"), appIdText).ConfigureAwait(false);

            // Achievements
            if (c.Achievements.Count > 0)
            {
                _log.Info("Downloading achievement images...");
                var imagePath = Path.Combine(settingsDir, "images");
                EnsureDirectory(imagePath);
                foreach (var ach in c.Achievements)
                {
                    await DownloadImageAsync(imagePath, ach.Icon);
                    await DownloadImageAsync(imagePath, ach.IconGray);
                    ach.Icon     = $"images/{Path.GetFileName(ach.Icon)}";
                    ach.IconGray = $"images/{Path.GetFileName(ach.IconGray)}";
                }

                _log.Info("Saving achievements...");
                var achievementJson = System.Text.Json.JsonSerializer.Serialize(
                    c.Achievements,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = true
                    });
                await File.WriteAllTextAsync(Path.Combine(settingsDir, "achievements.json"), achievementJson)
                    .ConfigureAwait(false);

                // Save unlock state to GSE Saves/<AppId>/achievements.json
                if (c.AppId > 0)
                {
                    var userSavesDir = Path.Combine(GlobalSettingsPath, c.AppId.ToString());
                    EnsureDirectory(userSavesDir);
                    var userAchievementsPath = Path.Combine(userSavesDir, "achievements.json");
                    var sb = new StringBuilder();
                    sb.AppendLine("{");
                    for (int i = 0; i < c.Achievements.Count; i++)
                    {
                        var ach   = c.Achievements[i];
                        var comma = i < c.Achievements.Count - 1 ? "," : "";
                        sb.AppendLine($"  \"{ach.Name}\": {{");
                        sb.AppendLine($"    \"earned\": {(ach.Unlocked ? "true" : "false")},");
                        sb.AppendLine($"    \"earned_time\": {(ach.Unlocked ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0)}");
                        sb.AppendLine($"  }}{comma}");
                    }
                    sb.AppendLine("}");
                    await File.WriteAllTextAsync(userAchievementsPath, sb.ToString()).ConfigureAwait(false);
                    _log.Info("Saved unlock state to GSE Saves.");
                }
            }
            else
            {
                _log.Info("No achievements set! Removing achievement files...");
                var imagePath       = Path.Combine(settingsDir, "images");
                var achievementPath = Path.Combine(settingsDir, "achievements.json");
                if (Directory.Exists(imagePath))    Directory.Delete(imagePath, true);
                if (File.Exists(achievementPath))   File.Delete(achievementPath);
            }

            // configs.app.ini — DLC lists
            var configsAppPath = Path.Combine(settingsDir, "configs.app.ini");
            if (c.DlcList.Count > 0)
            {
                _log.Info("Saving DLC settings to configs.app.ini...");
                var enabledDlcs  = c.DlcList.Where(x => x.Enabled).ToList();
                var disabledDlcs = c.DlcList.Where(x => !x.Enabled).ToList();
                var appPaths     = enabledDlcs.Where(x => !string.IsNullOrEmpty(x.AppPath)).ToList();

                var sb = new StringBuilder();
                sb.AppendLine("[app::dlcs]");
                sb.AppendLine("unlock_all=0");
                foreach (var dlc in enabledDlcs)  sb.AppendLine($"{dlc.AppId}={dlc.Name}");
                sb.AppendLine();

                if (disabledDlcs.Count > 0)
                {
                    sb.AppendLine("[app::dlcs_disabled]");
                    foreach (var dlc in disabledDlcs) sb.AppendLine($"{dlc.AppId}={dlc.Name}");
                    sb.AppendLine();
                }

                if (appPaths.Count > 0)
                {
                    sb.AppendLine("[app::paths]");
                    foreach (var dlc in appPaths) sb.AppendLine($"{dlc.AppId}={dlc.AppPath}");
                    sb.AppendLine();
                }

                await File.WriteAllTextAsync(configsAppPath, sb.ToString()).ConfigureAwait(false);
            }
            else
            {
                _log.Info("No DLC set! Removing configs.app.ini...");
                DeleteIfExists(configsAppPath);
                DeleteIfExists(Path.Combine(settingsDir, "DLC.txt"));
                DeleteIfExists(Path.Combine(settingsDir, "app_paths.txt"));
            }

            // configs.main.ini — connectivity flags (preserve unmanaged sections)
            var configsMainPath = Path.Combine(settingsDir, "configs.main.ini");
            var mainIni = File.Exists(configsMainPath)
                ? ReadIniFile(configsMainPath)
                : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (!mainIni.ContainsKey("main::connectivity"))
                mainIni["main::connectivity"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            mainIni["main::connectivity"]["offline"]            = c.Offline           ? "1" : "0";
            mainIni["main::connectivity"]["disable_networking"] = c.DisableNetworking ? "1" : "0";
            mainIni["main::connectivity"]["disable_overlay"]    = c.DisableOverlay    ? "1" : "0";

            await File.WriteAllTextAsync(configsMainPath, SerializeIni(mainIni)).ConfigureAwait(false);

            // Remove legacy flag .txt files from original Goldberg
            foreach (var flag in new[] { "offline.txt", "disable_networking.txt", "disable_overlay.txt" })
                DeleteIfExists(Path.Combine(settingsDir, flag));

            _log.Info("Save complete.");
        }

        // -----------------------------------------------------------------------
        // GoldbergApplied — quick check
        // -----------------------------------------------------------------------
        public bool GoldbergApplied(string path)
        {
            var settingsDirExists = Directory.Exists(Path.Combine(path, "steam_settings"));
            var appIdExists =
                File.Exists(Path.Combine(path, "steam_settings", "steam_appid.txt")) ||
                File.Exists(Path.Combine(path, "steam_appid.txt"));
            _log.Debug($"Goldberg applied? {settingsDirExists && appIdExists}");
            return settingsDirExists && appIdExists;
        }

        // -----------------------------------------------------------------------
        // Revert — restore game directory to pre-Goldberg state
        // -----------------------------------------------------------------------
        public async Task<bool> Revert(string path)
        {
            _log.Info($"Reverting Goldberg changes in {path}...");

            // Read AppID before deleting steam_settings so we can clean up GSE Saves
            var appIdFile = FirstExisting(
                Path.Combine(path, "steam_settings", "steam_appid.txt"),
                Path.Combine(path, "steam_appid.txt"));
            var appId = -1;
            if (appIdFile != null)
                int.TryParse(File.ReadLines(appIdFile).First().Trim(), out appId);

            await Task.Run(() =>
            {
                // Restore original steam_api DLLs
                foreach (var name in new[] { "steam_api", "steam_api64" })
                {
                    var currentDll  = Path.Combine(path, $"{name}.dll");
                    var originalDll = Path.Combine(path, $"{name}_o.dll");
                    var guiBackup   = Path.Combine(path, $".{name}.dll.GOLDBERGGUIBACKUP");

                    if (File.Exists(originalDll))
                    {
                        _log.Info($"Restoring original {name}.dll...");
                        DeleteIfExists(currentDll);
                        File.Move(originalDll, currentDll);
                        _log.Info($"Restored {name}.dll.");
                    }

                    if (File.Exists(guiBackup))
                    {
                        File.SetAttributes(guiBackup, FileAttributes.Normal);
                        File.Delete(guiBackup);
                    }
                }

                // Remove steam_settings folder
                var settingsDir = Path.Combine(path, "steam_settings");
                if (Directory.Exists(settingsDir))
                {
                    _log.Info("Removing steam_settings folder...");
                    Directory.Delete(settingsDir, true);
                }

                // Remove steam_appid.txt beside the DLL
                DeleteIfExists(Path.Combine(path, "steam_appid.txt"));

                // Remove GSE Saves achievements for this game
                if (appId > 0)
                {
                    var userAchievementsPath = Path.Combine(GlobalSettingsPath, appId.ToString(), "achievements.json");
                    if (File.Exists(userAchievementsPath))
                    {
                        _log.Info("Removing GSE Saves achievements...");
                        File.Delete(userAchievementsPath);
                    }
                }
            }).ConfigureAwait(false);

            _log.Info("Revert complete.");
            return true;
        }

        // -----------------------------------------------------------------------
        // Generate steam_interfaces.txt
        // -----------------------------------------------------------------------
        public async Task GenerateInterfacesFile(string filePath)
        {
            _log.Debug($"GenerateInterfacesFile {filePath}");
            var result     = new HashSet<string>();
            var dllContent = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);

            foreach (var name in _interfaceNames)
            {
                FindInterfaces(ref result, dllContent, new Regex($"{name}\\d{{3}}"));
                // SteamController has a special versioned and non-versioned variant
                if (!FindInterfaces(ref result, dllContent, new Regex(@"STEAMCONTROLLER_INTERFACE_VERSION\d{3}")))
                    FindInterfaces(ref result, dllContent, new Regex("STEAMCONTROLLER_INTERFACE_VERSION"));
            }

            var dirPath = Path.GetDirectoryName(filePath);
            if (dirPath == null) return;

            var steamSettingsDir = Path.Combine(dirPath, "steam_settings");
            EnsureDirectory(steamSettingsDir);
            var destPath = Path.Combine(steamSettingsDir, "steam_interfaces.txt");

            await using var destination = File.CreateText(destPath);
            foreach (var s in result)
                await destination.WriteLineAsync(s).ConfigureAwait(false);

            _log.Info($"Wrote steam_interfaces.txt to {destPath}");
        }

        // -----------------------------------------------------------------------
        // Supported languages
        // -----------------------------------------------------------------------
        public List<string> Languages() => new List<string>
        {
            DefaultLanguage, "arabic", "bulgarian", "schinese", "tchinese", "czech",
            "danish", "dutch", "finnish", "french", "german", "greek", "hungarian",
            "italian", "japanese", "koreana", "norwegian", "polish", "portuguese",
            "brazilian", "romanian", "russian", "spanish", "swedish", "thai",
            "turkish", "ukrainian"
        };

        // -----------------------------------------------------------------------
        // Private helpers — DLL copy/restore
        // -----------------------------------------------------------------------
        private void CopyDllFiles(string path, string name)
        {
            var steamApiDll = Path.Combine(path, $"{name}.dll");
            var originalDll = Path.Combine(path, $"{name}_o.dll");
            var guiBackup   = Path.Combine(path, $".{name}.dll.GOLDBERGGUIBACKUP");
            var goldbergDll = Path.Combine(_goldbergPath, $"{name}.dll");

            if (!File.Exists(originalDll))
            {
                _log.Info("Backing up original Steam API DLL...");
                File.Move(steamApiDll, originalDll);
            }
            else
            {
                File.Move(steamApiDll, guiBackup, true);
                File.SetAttributes(guiBackup, FileAttributes.Hidden);
            }

            _log.Info("Copying Goldberg DLL to target path...");
            File.Copy(goldbergDll, steamApiDll);
        }

        // -----------------------------------------------------------------------
        // Private helpers — download & extract
        // -----------------------------------------------------------------------
        private async Task<string> Download()
        {
            _log.Info("Checking for gbe_fork updates...");
            EnsureDirectory(_goldbergPath);

            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "GoldbergGUI");

            string downloadUrl = null;
            string remoteTag   = null;

            try
            {
                var json = await client.GetStringAsync(GbeReleaseApiUrl).ConfigureAwait(false);
                var doc  = System.Text.Json.JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                    remoteTag = tag.GetString();

                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out var nameProp)) continue;
                        var assetName = nameProp.GetString() ?? "";
                        if (assetName.Equals("emu-win-release.7z", StringComparison.OrdinalIgnoreCase) &&
                            asset.TryGetProperty("browser_download_url", out var url))
                        {
                            downloadUrl = url.GetString();
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _log.Error($"Failed to query GitHub API: {e.Message}");
                if (File.Exists(Path.Combine(_goldbergPath, "steam_api.dll")))
                {
                    _log.Warn("Using existing gbe_fork DLLs.");
                    return null;
                }
                ShowErrorMessage();
                return null;
            }

            if (downloadUrl == null)
            {
                _log.Error("Could not find emu-win-release.7z in the latest release!");
                ShowErrorMessage();
                return null;
            }

            // Compare with locally cached tag — skip download if already up to date
            var tagPath = Path.Combine(_goldbergPath, "release_tag");
            if (File.Exists(tagPath))
            {
                try
                {
                    var localTag = (await File.ReadAllTextAsync(tagPath).ConfigureAwait(false)).Trim();
                    if (localTag == remoteTag)
                    {
                        _log.Info("Latest gbe_fork already present, skipping download.");
                        return null;
                    }
                }
                catch
                {
                    _log.Error("Could not read local release tag, re-downloading.");
                }
            }

            _log.Info($"Downloading gbe_fork {remoteTag}...");
            await StartDownload(downloadUrl).ConfigureAwait(false);
            return remoteTag;
        }

        private async Task StartDownload(string downloadUrl)
        {
            try
            {
                var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "GoldbergGUI");
                _log.Debug($"Downloading from: {downloadUrl}");
                var data = await client.GetByteArrayAsync(downloadUrl).ConfigureAwait(false);
                await File.WriteAllBytesAsync(_goldbergZipPath, data).ConfigureAwait(false);
                _log.Info("Download finished!");
            }
            catch (Exception e)
            {
                ShowErrorMessage();
                _log.Error(e.ToString());
                Environment.Exit(1);
            }
        }

        private async Task Extract(string archivePath)
        {
            _log.Debug("Starting extraction...");
            if (Directory.Exists(_goldbergPath)) Directory.Delete(_goldbergPath, true);
            EnsureDirectory(_goldbergPath);

            try
            {
                await Task.Run(() =>
                {
                    using var archive = SharpCompress.Archives.SevenZip.SevenZipArchive.Open(archivePath);
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.IsDirectory) continue;
                        try
                        {
                            var fileName = Path.GetFileName(entry.Key);
                            var isSteamDll =
                                !string.IsNullOrEmpty(fileName) &&
                                fileName.StartsWith("steam_api", StringComparison.OrdinalIgnoreCase) &&
                                fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

                            var destPath = isSteamDll
                                ? Path.Combine(_goldbergPath, fileName)
                                : Path.Combine(_goldbergPath, entry.Key);

                            EnsureDirectory(Path.GetDirectoryName(destPath));
                            entry.WriteToFile(destPath, new ExtractionOptions { Overwrite = true });
                        }
                        catch (Exception e)
                        {
                            _log.Error($"Error extracting {entry.Key}: {e.Message}");
                        }
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Error($"Failed to open archive: {e.Message}");
                ShowErrorMessage();
                return;
            }

            var x86 = Path.Combine(_goldbergPath, "steam_api.dll");
            var x64 = Path.Combine(_goldbergPath, "steam_api64.dll");
            if (File.Exists(x86) || File.Exists(x64))
                _log.Info("Extraction successful!");
            else
            {
                _log.Warn("DLLs not found after extraction!");
                ShowErrorMessage();
            }
        }

        private async Task DownloadImageAsync(string imageFolder, string imageUrl)
        {
            var fileName   = Path.GetFileName(imageUrl);
            var targetPath = Path.Combine(imageFolder, fileName);
            if (File.Exists(targetPath)) return;
            if (imageUrl.StartsWith("images/"))
            {
                _log.Warn($"Previously downloaded image '{imageUrl}' is now missing!");
                return;
            }
            var client    = new HttpClient();
            var imageData = await client.GetByteArrayAsync(new Uri(imageUrl, UriKind.Absolute));
            await File.WriteAllBytesAsync(targetPath, imageData);
        }

        private void ShowErrorMessage()
        {
            if (Directory.Exists(_goldbergPath)) Directory.Delete(_goldbergPath, true);
            EnsureDirectory(_goldbergPath);
            MessageBox.Show(
                "Could not set up gbe_fork!\n" +
                "Download it manually from https://github.com/Detanup01/gbe_fork/releases\n" +
                "and extract its contents into the \"goldberg\" subfolder.");
        }

        // -----------------------------------------------------------------------
        // Private helpers — INI parsing
        // -----------------------------------------------------------------------
        private static Dictionary<string, Dictionary<string, string>> ReadIniFile(string path)
        {
            var result         = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var currentSection = "";
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key   = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[currentSection][key] = value;
                }
            }
            return result;
        }

        private static string SerializeIni(Dictionary<string, Dictionary<string, string>> ini)
        {
            var sb = new StringBuilder();
            foreach (var section in ini)
            {
                sb.AppendLine($"[{section.Key}]");
                foreach (var kv in section.Value) sb.AppendLine($"{kv.Key}={kv.Value}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static bool IniFlag(Dictionary<string, string> section, string key)
        {
            if (!section.TryGetValue(key, out var val)) return false;
            val = val.Trim();
            return val == "1" ||
                   val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   val.Equals("yes",  StringComparison.OrdinalIgnoreCase);
        }

        // -----------------------------------------------------------------------
        // Private helpers — misc
        // -----------------------------------------------------------------------
        private static bool FindInterfaces(ref HashSet<string> result, string dllContent, Regex regex)
        {
            var found = false;
            foreach (Match match in regex.Matches(dllContent))
            {
                found = true;
                result.Add(match.Value);
            }
            return found;
        }

        private static bool IsValidSteamId(long id) => id >= SteamIdMin && id <= SteamIdMax;

        private static void EnsureDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path)) Directory.CreateDirectory(path);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Returns the first path that exists, or null if none do.</summary>
        private static string FirstExisting(params string[] paths) =>
            paths.FirstOrDefault(File.Exists);
    }
}
