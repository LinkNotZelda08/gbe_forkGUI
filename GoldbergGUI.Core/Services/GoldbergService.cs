using GoldbergGUI.Core.Models;
using GoldbergGUI.Core.Utils;
using MvvmCross.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GoldbergGUI.Core.Services
{
    // downloads and updates goldberg emu (gbe_fork)
    // sets up config files in gbe_fork INI format
    // does file copy stuff
    public interface IGoldbergService
    {
        public Task<GoldbergGlobalConfiguration> Initialize(IMvxLog log);
        public Task<GoldbergConfiguration> Read(string path);
        public Task Save(string path, GoldbergConfiguration configuration);
        public Task<GoldbergGlobalConfiguration> GetGlobalSettings();
        public Task SetGlobalSettings(GoldbergGlobalConfiguration configuration);
        public bool GoldbergApplied(string path);
        public Task<bool> Revert(string path);
        public Task GenerateInterfacesFile(string filePath);
        public List<string> Languages();
    }

    // ReSharper disable once UnusedType.Global
    // ReSharper disable once ClassNeverInstantiated.Global
    public class GoldbergService : IGoldbergService
    {
        private IMvxLog _log;
        private const string DefaultAccountName = "Mr_Goldberg";
        private const long DefaultSteamId = 76561197960287930;
        private const string DefaultLanguage = "english";

        // gbe_fork release page (GitHub API)
        private const string GbeReleaseApiUrl =
            "https://api.github.com/repos/Detanup01/gbe_fork/releases/latest";

        private readonly string _goldbergZipPath =
            Path.Combine(Directory.GetCurrentDirectory(), "goldberg.zip");
        private readonly string _goldbergPath =
            Path.Combine(Directory.GetCurrentDirectory(), "goldberg");

        // gbe_fork uses "GSE Saves" instead of "Goldberg SteamEmu Saves"
        private static readonly string GlobalSettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GSE Saves");

        // Global settings live in GSE Saves/settings/configs.user.ini
        private readonly string _globalUserIniPath =
            Path.Combine(GlobalSettingsPath, "settings", "configs.user.ini");

        // ReSharper disable StringLiteralTypo
        private readonly List<string> _interfaceNames = new List<string>
        {
            "SteamClient",
            "SteamGameServer",
            "SteamGameServerStats",
            "SteamUser",
            "SteamFriends",
            "SteamUtils",
            "SteamMatchMaking",
            "SteamMatchMakingServers",
            "STEAMUSERSTATS_INTERFACE_VERSION",
            "STEAMAPPS_INTERFACE_VERSION",
            "SteamNetworking",
            "STEAMREMOTESTORAGE_INTERFACE_VERSION",
            "STEAMSCREENSHOTS_INTERFACE_VERSION",
            "STEAMHTTP_INTERFACE_VERSION",
            "STEAMUNIFIEDMESSAGES_INTERFACE_VERSION",
            "STEAMUGC_INTERFACE_VERSION",
            "STEAMAPPLIST_INTERFACE_VERSION",
            "STEAMMUSIC_INTERFACE_VERSION",
            "STEAMMUSICREMOTE_INTERFACE_VERSION",
            "STEAMHTMLSURFACE_INTERFACE_VERSION_",
            "STEAMINVENTORY_INTERFACE_V",
            "SteamController",
            "SteamMasterServerUpdater",
            "STEAMVIDEO_INTERFACE_V"
        };

        public async Task<GoldbergGlobalConfiguration> Initialize(IMvxLog log)
        {
            _log = log;
            var downloadedTag = await Download().ConfigureAwait(false);
            if (downloadedTag != null)
            {
                await Extract(_goldbergZipPath).ConfigureAwait(false);
                // Only save the tag after successful extraction
                var tagPath = Path.Combine(_goldbergPath, "release_tag");
                var x86Dll = Path.Combine(_goldbergPath, "steam_api.dll");
                var x64Dll = Path.Combine(_goldbergPath, "steam_api64.dll");
                if (File.Exists(x86Dll) || File.Exists(x64Dll))
                    await File.WriteAllTextAsync(tagPath, downloadedTag).ConfigureAwait(false);
            }
            return await GetGlobalSettings().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // Global settings: read from GSE Saves/settings/configs.user.ini
        // -----------------------------------------------------------------------
        public async Task<GoldbergGlobalConfiguration> GetGlobalSettings()
        {
            _log.Info("Getting global settings...");
            var accountName = DefaultAccountName;
            var steamId = DefaultSteamId;
            var language = DefaultLanguage;
            var customBroadcastIps = new List<string>();

            var settingsDir = Path.GetDirectoryName(_globalUserIniPath);
            if (!Directory.Exists(settingsDir))
                Directory.CreateDirectory(settingsDir!);

            if (File.Exists(_globalUserIniPath))
            {
                await Task.Run(() =>
                {
                    var ini = ReadIniFile(_globalUserIniPath);

                    // [user::general]
                    //   account_name    = ...
                    //   account_steamid = ...
                    //   language        = ...
                    if (ini.TryGetValue("user::general", out var general))
                    {
                        if (general.TryGetValue("account_name", out var name) &&
                            !string.IsNullOrWhiteSpace(name))
                            accountName = name.Trim();

                        if (general.TryGetValue("account_steamid", out var sid) &&
                            long.TryParse(sid.Trim(), out var parsedId) &&
                            parsedId >= 76561197960265729 && parsedId <= 76561202255233023)
                            steamId = parsedId;

                        if (general.TryGetValue("language", out var lang) &&
                            !string.IsNullOrWhiteSpace(lang))
                            language = lang.Trim();
                    }

                    // [user::saves]
                    //   custom_broadcasts = ip1,ip2,...
                    if (ini.TryGetValue("user::saves", out var saves) &&
                        saves.TryGetValue("custom_broadcasts", out var broadcasts) &&
                        !string.IsNullOrWhiteSpace(broadcasts))
                    {
                        customBroadcastIps.AddRange(
                            broadcasts.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(x => x.Trim()));
                    }
                }).ConfigureAwait(false);
            }

            _log.Info("Got global settings.");
            return new GoldbergGlobalConfiguration
            {
                AccountName = accountName,
                UserSteamId = steamId,
                Language = language,
                CustomBroadcastIps = customBroadcastIps
            };
        }

        // -----------------------------------------------------------------------
        // Global settings: write to GSE Saves/settings/configs.user.ini
        // -----------------------------------------------------------------------
        public async Task SetGlobalSettings(GoldbergGlobalConfiguration c)
        {
            _log.Info("Setting global settings...");

            var accountName = string.IsNullOrEmpty(c.AccountName) ? DefaultAccountName : c.AccountName;
            var userSteamId = (c.UserSteamId >= 76561197960265729 && c.UserSteamId <= 76561202255233023)
                ? c.UserSteamId : DefaultSteamId;
            var language = string.IsNullOrEmpty(c.Language) ? DefaultLanguage : c.Language;

            var settingsDir = Path.GetDirectoryName(_globalUserIniPath);
            if (!Directory.Exists(settingsDir))
                Directory.CreateDirectory(settingsDir!);

            var sb = new StringBuilder();
            sb.AppendLine("[user::general]");
            sb.AppendLine($"account_name={accountName}");
            sb.AppendLine($"account_steamid={userSteamId}");
            sb.AppendLine($"language={language}");
            sb.AppendLine();

            if (c.CustomBroadcastIps != null && c.CustomBroadcastIps.Count > 0)
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
            var appId = -1;
            var achievementList = new List<Achievement>();
            var dlcList = new List<DlcApp>();

            // gbe_fork checks steam_settings/steam_appid.txt first, then game dir
            var steamAppidInSettings = Path.Combine(path, "steam_settings", "steam_appid.txt");
            var steamAppidLegacy     = Path.Combine(path, "steam_appid.txt");
            var appIdFile = File.Exists(steamAppidInSettings) ? steamAppidInSettings
                          : File.Exists(steamAppidLegacy)     ? steamAppidLegacy
                          : null;

            if (appIdFile != null)
            {
                _log.Info("Getting AppID...");
                await Task.Run(() =>
                    int.TryParse(File.ReadLines(appIdFile).First().Trim(), out appId)
                ).ConfigureAwait(false);
            }
            else
            {
                _log.Info("steam_appid.txt missing! Skipping...");
            }

            // achievements.json — defines achievement metadata
            var achievementJson = Path.Combine(path, "steam_settings", "achievements.json");
            if (File.Exists(achievementJson))
            {
                _log.Info("Getting achievements...");
                var json = await File.ReadAllTextAsync(achievementJson).ConfigureAwait(false);
                achievementList = System.Text.Json.JsonSerializer.Deserialize<List<Achievement>>(json);

                // Also read unlock state from GSE Saves/<AppId>/achievements.json
                if (appId > 0)
                {
                    var userAchievementsPath = Path.Combine(GlobalSettingsPath, appId.ToString(),
                        "achievements.json");
                    if (File.Exists(userAchievementsPath))
                    {
                        _log.Info("Reading unlocked achievements from GSE Saves...");
                        var userJson = await File.ReadAllTextAsync(userAchievementsPath).ConfigureAwait(false);
                        var userDoc = System.Text.Json.JsonDocument.Parse(userJson);
                        foreach (var achievement in achievementList)
                        {
                            if (userDoc.RootElement.TryGetProperty(achievement.Name, out var achEl) &&
                                achEl.TryGetProperty("earned", out var earned))
                            {
                                achievement.Unlocked = earned.GetBoolean();
                            }
                        }
                    }
                }
            }
            else
            {
                _log.Info("\"steam_settings/achievements.json\" missing! Skipping...");
            }

            // DLC: gbe_fork uses configs.app.ini [app::dlcs]; fall back to DLC.txt
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
                            if (int.TryParse(kv.Key, out var dlcId))
                                dlcList.Add(new DlcApp { AppId = dlcId, Name = kv.Value, Enabled = true });

                    // Read disabled DLCs (stored separately so they can be re-enabled later)
                    if (ini.TryGetValue("app::dlcs_disabled", out var disabledDlcs))
                        foreach (var kv in disabledDlcs)
                            if (int.TryParse(kv.Key, out var dlcId))
                                dlcList.Add(new DlcApp { AppId = dlcId, Name = kv.Value, Enabled = false });

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
                var lines = await File.ReadAllLinesAsync(dlcTxtLegacy).ConfigureAwait(false);
                var expression = new Regex(@"(?<id>.*) *= *(?<n>.*)");
                foreach (var line in lines)
                {
                    var match = expression.Match(line);
                    if (match.Success)
                        dlcList.Add(new DlcApp
                        {
                            AppId = Convert.ToInt32(match.Groups["id"].Value),
                            Name  = match.Groups["n"].Value,
                            Enabled = true
                        });
                }
                if (File.Exists(appPathTxt))
                {
                    var appPathLines = await File.ReadAllLinesAsync(appPathTxt).ConfigureAwait(false);
                    var appPathExpr  = new Regex(@"(?<id>.*) *= *(?<appPath>.*)");
                    foreach (var line in appPathLines)
                    {
                        var match = appPathExpr.Match(line);
                        if (!match.Success) continue;
                        var i = dlcList.FindIndex(x =>
                            x.AppId.Equals(Convert.ToInt32(match.Groups["id"].Value)));
                        if (i >= 0) dlcList[i].AppPath = match.Groups["appPath"].Value;
                    }
                }
            }
            else
            {
                _log.Info("No DLC config found! Skipping...");
            }

            // Connectivity flags: read from configs.main.ini [main::connectivity]
            var offline = false;
            var disableNetworking = false;
            var disableOverlay = false;
            var configsMainIni = Path.Combine(path, "steam_settings", "configs.main.ini");

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
                // Legacy flag files (original Goldberg) — read for backwards compat
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

            // DLL setup
            _log.Info("Running DLL setup...");
            const string x86Name = "steam_api";
            const string x64Name = "steam_api64";
            if (File.Exists(Path.Combine(path, $"{x86Name}.dll"))) CopyDllFiles(path, x86Name);
            if (File.Exists(Path.Combine(path, $"{x64Name}.dll"))) CopyDllFiles(path, x64Name);
            _log.Info("DLL setup finished!");

            var settingsDir = Path.Combine(path, "steam_settings");
            if (!Directory.Exists(settingsDir))
                Directory.CreateDirectory(settingsDir);

            // steam_appid.txt: put in steam_settings/ (gbe_fork) AND beside DLL (compat)
            await File.WriteAllTextAsync(Path.Combine(settingsDir, "steam_appid.txt"),
                c.AppId.ToString()).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(path, "steam_appid.txt"),
                c.AppId.ToString()).ConfigureAwait(false);

            // Achievements + images
            if (c.Achievements.Count > 0)
            {
                _log.Info("Downloading images...");
                var imagePath = Path.Combine(settingsDir, "images");
                Directory.CreateDirectory(imagePath);
                foreach (var achievement in c.Achievements)
                {
                    await DownloadImageAsync(imagePath, achievement.Icon);
                    await DownloadImageAsync(imagePath, achievement.IconGray);
                    achievement.Icon     = $"images/{Path.GetFileName(achievement.Icon)}";
                    achievement.IconGray = $"images/{Path.GetFileName(achievement.IconGray)}";
                }
                _log.Info("Saving achievements...");
                var achievementJson = System.Text.Json.JsonSerializer.Serialize(
                    c.Achievements,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = true
                    });
                await File.WriteAllTextAsync(
                    Path.Combine(settingsDir, "achievements.json"), achievementJson
                ).ConfigureAwait(false);
                _log.Info("Finished saving achievements.");

                // Save unlock state to GSE Saves/<AppId>/achievements.json
                // Format: { "ACH_NAME": { "earned": true/false, "earned_time": 0 }, ... }
                if (c.AppId > 0)
                {
                    var userSavesDir = Path.Combine(GlobalSettingsPath, c.AppId.ToString());
                    Directory.CreateDirectory(userSavesDir);
                    var userAchievementsPath = Path.Combine(userSavesDir, "achievements.json");
                    var sb = new StringBuilder();
                    sb.AppendLine("{");
                    for (int i = 0; i < c.Achievements.Count; i++)
                    {
                        var ach = c.Achievements[i];
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
                var imagePath = Path.Combine(settingsDir, "images");
                if (Directory.Exists(imagePath)) Directory.Delete(imagePath, true);
                var achievementPath = Path.Combine(settingsDir, "achievements.json");
                if (File.Exists(achievementPath)) File.Delete(achievementPath);
                _log.Info("Removed achievement files.");
            }

            // ---- configs.app.ini ([app::dlcs] + [app::dlcs_disabled] + [app::paths]) ----
            var configsAppPath = Path.Combine(settingsDir, "configs.app.ini");
            if (c.DlcList.Count > 0)
            {
                _log.Info("Saving DLC settings to configs.app.ini...");
                var sb = new StringBuilder();

                var enabledDlcs  = c.DlcList.Where(x => x.Enabled).ToList();
                var disabledDlcs = c.DlcList.Where(x => !x.Enabled).ToList();

                // Only enabled DLCs go into [app::dlcs] — gbe_fork reads these
                // unlock_all=0 tells gbe_fork to only unlock the listed DLCs
                sb.AppendLine("[app::dlcs]");
                sb.AppendLine("unlock_all=0");
                foreach (var dlc in enabledDlcs)
                    sb.AppendLine($"{dlc.AppId}={dlc.Name}");
                sb.AppendLine();

                // Disabled DLCs stored separately so they survive a reload and can be re-enabled
                if (disabledDlcs.Count > 0)
                {
                    sb.AppendLine("[app::dlcs_disabled]");
                    foreach (var dlc in disabledDlcs)
                        sb.AppendLine($"{dlc.AppId}={dlc.Name}");
                    sb.AppendLine();
                }

                var appPaths = c.DlcList.Where(x => x.Enabled && !string.IsNullOrEmpty(x.AppPath)).ToList();
                if (appPaths.Count > 0)
                {
                    sb.AppendLine("[app::paths]");
                    foreach (var dlc in appPaths)
                        sb.AppendLine($"{dlc.AppId}={dlc.AppPath}");
                    sb.AppendLine();
                }
                await File.WriteAllTextAsync(configsAppPath, sb.ToString()).ConfigureAwait(false);
            }
            else
            {
                _log.Info("No DLC set! Removing configs.app.ini...");
                if (File.Exists(configsAppPath)) File.Delete(configsAppPath);
                // Clean up legacy files too
                var legacyDlc  = Path.Combine(settingsDir, "DLC.txt");
                var legacyPath = Path.Combine(settingsDir, "app_paths.txt");
                if (File.Exists(legacyDlc))  File.Delete(legacyDlc);
                if (File.Exists(legacyPath)) File.Delete(legacyPath);
            }

            // ---- configs.main.ini ([main::connectivity]) ----
            var configsMainPath = Path.Combine(settingsDir, "configs.main.ini");
            // Preserve any existing sections we don't manage
            var mainIni = File.Exists(configsMainPath)
                ? ReadIniFile(configsMainPath)
                : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (!mainIni.ContainsKey("main::connectivity"))
                mainIni["main::connectivity"] =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            mainIni["main::connectivity"]["offline"]            = c.Offline           ? "1" : "0";
            mainIni["main::connectivity"]["disable_networking"] = c.DisableNetworking ? "1" : "0";
            mainIni["main::connectivity"]["disable_overlay"]    = c.DisableOverlay    ? "1" : "0";

            await File.WriteAllTextAsync(configsMainPath, SerializeIni(mainIni)).ConfigureAwait(false);

            // Remove old flag .txt files from original Goldberg if present
            foreach (var flagFile in new[] { "offline.txt", "disable_networking.txt", "disable_overlay.txt" })
            {
                var fp = Path.Combine(settingsDir, flagFile);
                if (File.Exists(fp)) File.Delete(fp);
            }

            _log.Info("Save complete.");
        }

        private void CopyDllFiles(string path, string name)
        {
            var steamApiDll = Path.Combine(path, $"{name}.dll");
            var originalDll = Path.Combine(path, $"{name}_o.dll");
            var guiBackup   = Path.Combine(path, $".{name}.dll.GOLDBERGGUIBACKUP");
            var goldbergDll = Path.Combine(_goldbergPath, $"{name}.dll");

            if (!File.Exists(originalDll))
            {
                _log.Info("Back up original Steam API DLL...");
                File.Move(steamApiDll, originalDll);
            }
            else
            {
                File.Move(steamApiDll, guiBackup, true);
                File.SetAttributes(guiBackup, FileAttributes.Hidden);
            }

            _log.Info("Copy Goldberg DLL to target path...");
            File.Copy(goldbergDll, steamApiDll);
        }

        public bool GoldbergApplied(string path)
        {
            var steamSettingsDirExists = Directory.Exists(Path.Combine(path, "steam_settings"));
            var steamAppIdTxtExists =
                File.Exists(Path.Combine(path, "steam_settings", "steam_appid.txt")) ||
                File.Exists(Path.Combine(path, "steam_appid.txt"));
            _log.Debug($"Goldberg applied? {steamSettingsDirExists && steamAppIdTxtExists}");
            return steamSettingsDirExists && steamAppIdTxtExists;
        }

        // -----------------------------------------------------------------------
        // Revert: restore the game directory to its original pre-Goldberg state
        // -----------------------------------------------------------------------
        public async Task<bool> Revert(string path)
        {
            _log.Info($"Reverting Goldberg changes in {path}...");

            // Read the AppID before we delete steam_settings so we know which GSE Saves to clean up
            var appId = -1;
            var steamAppidInSettings = Path.Combine(path, "steam_settings", "steam_appid.txt");
            var steamAppidLegacy     = Path.Combine(path, "steam_appid.txt");
            var appIdFile = File.Exists(steamAppidInSettings) ? steamAppidInSettings
                          : File.Exists(steamAppidLegacy)     ? steamAppidLegacy
                          : null;
            if (appIdFile != null)
                int.TryParse(File.ReadLines(appIdFile).First().Trim(), out appId);

            await Task.Run(() =>
            {
                // Restore original steam_api.dll if backup exists
                foreach (var name in new[] { "steam_api", "steam_api64" })
                {
                    var currentDll  = Path.Combine(path, $"{name}.dll");
                    var originalDll = Path.Combine(path, $"{name}_o.dll");
                    var guiBackup   = Path.Combine(path, $".{name}.dll.GOLDBERGGUIBACKUP");

                    if (File.Exists(originalDll))
                    {
                        _log.Info($"Restoring original {name}.dll...");
                        if (File.Exists(currentDll)) File.Delete(currentDll);
                        File.Move(originalDll, currentDll);
                        _log.Info($"Restored {name}.dll.");
                    }

                    // Clean up any GUI backup file
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
                var steamAppId = Path.Combine(path, "steam_appid.txt");
                if (File.Exists(steamAppId))
                {
                    _log.Info("Removing steam_appid.txt...");
                    File.Delete(steamAppId);
                }

                // Remove GSE Saves achievements for this game
                if (appId > 0)
                {
                    var userAchievementsPath = Path.Combine(GlobalSettingsPath, appId.ToString(),
                        "achievements.json");
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
        // Download latest gbe_fork release from GitHub Releases API
        // -----------------------------------------------------------------------
        private async Task<string> Download()
        {
            _log.Info("Checking for gbe_fork updates...");
            if (!Directory.Exists(_goldbergPath)) Directory.CreateDirectory(_goldbergPath);

            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "GoldbergGUI");

            string downloadUrl = null;
            string remoteTag = null;

            try
            {
                var json = await client.GetStringAsync(GbeReleaseApiUrl).ConfigureAwait(false);
                var doc = System.Text.Json.JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                    remoteTag = tag.GetString();

                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out var nameProp)) continue;
                        var assetName = nameProp.GetString() ?? "";
                        if (assetName.Equals("emu-win-release.7z", StringComparison.OrdinalIgnoreCase))
                        {
                            if (asset.TryGetProperty("browser_download_url", out var url))
                            {
                                downloadUrl = url.GetString();
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _log.Error($"Failed to query GitHub API: {e.Message}");
                // If we already have DLLs, just continue with what we have
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

            // Compare with locally cached tag
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
                catch (Exception)
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
            if (Directory.Exists(_goldbergPath))
                Directory.Delete(_goldbergPath, true);
            Directory.CreateDirectory(_goldbergPath);

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
                            var destPath =
                                (!string.IsNullOrEmpty(fileName) &&
                                fileName.StartsWith("steam_api", StringComparison.OrdinalIgnoreCase) &&
                                fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                ? Path.Combine(_goldbergPath, fileName)
                                : Path.Combine(_goldbergPath, entry.Key);

                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
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

            // Verify the DLLs actually landed correctly instead of relying on the error flag
            var x86 = Path.Combine(_goldbergPath, "steam_api.dll");
            var x64 = Path.Combine(_goldbergPath, "steam_api64.dll");
            if (File.Exists(x86) || File.Exists(x64))
            {
                _log.Info("Extraction successful!");
            }
            else
            {
                _log.Warn("DLLs not found after extraction!");
                ShowErrorMessage();
            }
        }
        private void ShowErrorMessage()
        {
            if (Directory.Exists(_goldbergPath))
                Directory.Delete(_goldbergPath, true);
            Directory.CreateDirectory(_goldbergPath);
            MessageBox.Show(
                "Could not set up gbe_fork!\n" +
                "Download it manually from https://github.com/Detanup01/gbe_fork/releases\n" +
                "and extract its contents into the \"goldberg\" subfolder.");
        }
        
        // -----------------------------------------------------------------------
        // Generate steam_interfaces.txt
        // gbe_fork requires this file inside steam_settings/ (not beside the DLL)
        // -----------------------------------------------------------------------
        public async Task GenerateInterfacesFile(string filePath)
        {
            _log.Debug($"GenerateInterfacesFile {filePath}");
            var result = new HashSet<string>();
            var dllContent = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            foreach (var name in _interfaceNames)
            {
                FindInterfaces(ref result, dllContent, new Regex($"{name}\\d{{3}}"));
                if (!FindInterfaces(ref result, dllContent, new Regex(@"STEAMCONTROLLER_INTERFACE_VERSION\d{3}")))
                    FindInterfaces(ref result, dllContent, new Regex("STEAMCONTROLLER_INTERFACE_VERSION"));
            }

            var dirPath = Path.GetDirectoryName(filePath);
            if (dirPath == null) return;

            // Must go into steam_settings/
            var steamSettingsDir = Path.Combine(dirPath, "steam_settings");
            Directory.CreateDirectory(steamSettingsDir);
            var destPath = Path.Combine(steamSettingsDir, "steam_interfaces.txt");

            await using var destination = File.CreateText(destPath);
            foreach (var s in result)
                await destination.WriteLineAsync(s).ConfigureAwait(false);

            _log.Info($"Wrote steam_interfaces.txt to {destPath}");
        }

        public List<string> Languages() => new List<string>
        {
            DefaultLanguage,
            "arabic",
            "bulgarian",
            "schinese",
            "tchinese",
            "czech",
            "danish",
            "dutch",
            "finnish",
            "french",
            "german",
            "greek",
            "hungarian",
            "italian",
            "japanese",
            "koreana",
            "norwegian",
            "polish",
            "portuguese",
            "brazilian",
            "romanian",
            "russian",
            "spanish",
            "swedish",
            "thai",
            "turkish",
            "ukrainian"
        };

        // -----------------------------------------------------------------------
        // Minimal INI helpers — no external dependency needed
        // -----------------------------------------------------------------------
        private static Dictionary<string, Dictionary<string, string>> ReadIniFile(string path)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var currentSection = "";
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#"))
                    continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] =
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key   = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] =
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                foreach (var kv in section.Value)
                    sb.AppendLine($"{kv.Key}={kv.Value}");
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

        private static bool FindInterfaces(ref HashSet<string> result, string dllContent, Regex regex)
        {
            var success = false;
            foreach (Match match in regex.Matches(dllContent))
            {
                success = true;
                result.Add(match.Value);
            }
            return success;
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
            var httpClient = new HttpClient();
            var imageData = await httpClient.GetByteArrayAsync(new Uri(imageUrl, UriKind.Absolute));
            await File.WriteAllBytesAsync(targetPath, imageData);
        }
    }
}
