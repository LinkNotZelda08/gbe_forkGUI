using GoldbergGUI.Core.Models;
using GoldbergGUI.Core.Services;
using GoldbergGUI.Core.Utils;
using Microsoft.Win32;
using MvvmCross.Commands;
using MvvmCross.Logging;
using MvvmCross.Navigation;
using MvvmCross.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace GoldbergGUI.Core.ViewModels
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class MainViewModel : MvxNavigationViewModel
    {
        // -----------------------------------------------------------------------
        // Services & infrastructure
        // -----------------------------------------------------------------------
        private readonly ISteamService _steam;
        private readonly IGoldbergService _goldberg;
        private readonly IThemeService _theme;
        private readonly IMvxLog _log;
        private readonly IMvxLogProvider _logProvider;
        private readonly IMvxNavigationService _navigationService;

        // -----------------------------------------------------------------------
        // Backing fields
        // -----------------------------------------------------------------------
        private string _dllPath;
        private string _gameName;
        private int _appId;
        private ObservableCollection<Achievement> _achievements;
        private bool? _allAchievementsUnlocked = false;
        private ObservableCollection<DlcApp> _dlcs;
        private bool? _allDlcEnabled = true;
        private string _accountName;
        private long _steamId;
        private bool _offline;
        private bool _disableNetworking;
        private bool _disableOverlay;
        private string _statusText;
        private bool _mainWindowEnabled;
        private bool _goldbergApplied;
        private ObservableCollection<string> _steamLanguages;
        private string _selectedLanguage;
        private ObservableCollection<string> _themes;
        private string _selectedTheme;

        // Placeholder sentinel so we can tell whether a real path has been set
        private const string DllPathPlaceholder = "Path to game's steam_api(64).dll...";
        private const string GameNamePlaceholder = "Game name...";

        public MainViewModel(ISteamService steam, IGoldbergService goldberg, IThemeService theme,
            IMvxLogProvider logProvider, IMvxNavigationService navigationService)
            : base(logProvider, navigationService)
        {
            _steam = steam;
            _goldberg = goldberg;
            _theme = theme;
            _logProvider = logProvider;
            _log = logProvider.GetLogFor<MainViewModel>();
            _navigationService = navigationService;
        }

        // -----------------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------------

        public override void Prepare()
        {
            base.Prepare();
            Task.Run(async () =>
            {
                MainWindowEnabled = false;
                StatusText = "Initializing! Please wait...";
                try
                {
                    SteamLanguages = new ObservableCollection<string>(_goldberg.Languages());
                    Themes = new ObservableCollection<string>(_theme.Themes);

                    var savedTheme = _theme.LoadSavedTheme();
                    _theme.ApplyTheme(savedTheme);
                    _selectedTheme = savedTheme;
                    RaisePropertyChanged(() => SelectedTheme);

                    ResetForm();

                    await _steam.Initialize(_logProvider.GetLogFor<SteamService>()).ConfigureAwait(false);
                    var globalConfig = await _goldberg.Initialize(_logProvider.GetLogFor<GoldbergService>()).ConfigureAwait(false);

                    AccountName = globalConfig.AccountName;
                    SteamId = globalConfig.UserSteamId;
                    SelectedLanguage = globalConfig.Language;
                }
                catch (Exception e)
                {
                    _log.Error(e.Message);
                    throw;
                }

                MainWindowEnabled = true;
                StatusText = "Ready.";
            });
        }

        public override async Task Initialize()
        {
            await base.Initialize().ConfigureAwait(false);
        }

        // -----------------------------------------------------------------------
        // Properties — game / DLL
        // -----------------------------------------------------------------------

        public string DllPath
        {
            get => _dllPath;
            private set
            {
                _dllPath = value;
                RaisePropertyChanged(() => DllPath);
                RaisePropertyChanged(() => DllSelected);
                RaisePropertyChanged(() => SteamInterfacesTxtExists);
            }
        }

        public string GameName
        {
            get => _gameName;
            set
            {
                _gameName = value;
                RaisePropertyChanged(() => GameName);
            }
        }

        public int AppId
        {
            get => _appId;
            set
            {
                _appId = value;
                RaisePropertyChanged(() => AppId);
                Task.Run(async () => await GetNameById().ConfigureAwait(false));
            }
        }

        // -----------------------------------------------------------------------
        // Properties — DLC
        // -----------------------------------------------------------------------

        // ReSharper disable once InconsistentNaming
        public ObservableCollection<DlcApp> DLCs
        {
            get => _dlcs;
            set
            {
                UnsubscribeDlcEvents(_dlcs);
                _dlcs = value;
                SubscribeDlcEvents(_dlcs);
                RaisePropertyChanged(() => DLCs);
                UpdateAllDlcEnabledState();
            }
        }

        public bool? AllDlcEnabled
        {
            get => _allDlcEnabled;
            set
            {
                // Toggle: all-on or mixed → turn everything off; all-off → turn everything on.
                bool newValue = _allDlcEnabled != true;
                _allDlcEnabled = newValue;
                RaisePropertyChanged(() => AllDlcEnabled);
                SetAllDlcEnabled(newValue);
            }
        }

        private void SubscribeDlcEvents(IEnumerable<DlcApp> items)
        {
            if (items == null) return;
            foreach (var dlc in items) dlc.PropertyChanged += OnDlcPropertyChanged;
        }

        private void UnsubscribeDlcEvents(IEnumerable<DlcApp> items)
        {
            if (items == null) return;
            foreach (var dlc in items) dlc.PropertyChanged -= OnDlcPropertyChanged;
        }

        private void OnDlcPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DlcApp.Enabled))
                UpdateAllDlcEnabledState();
        }

        private void UpdateAllDlcEnabledState()
        {
            _allDlcEnabled = (_dlcs == null || _dlcs.Count == 0) ? true
                           : _dlcs.All(x => x.Enabled)          ? true
                           : _dlcs.All(x => !x.Enabled)         ? false
                           : (bool?)null; // mixed → indeterminate
            RaisePropertyChanged(() => AllDlcEnabled);
        }

        private void SetAllDlcEnabled(bool enabled)
        {
            if (DLCs == null) return;
            // Unsubscribe while bulk-updating to avoid per-item state recalculation
            UnsubscribeDlcEvents(DLCs);
            foreach (var dlc in DLCs) dlc.Enabled = enabled;
            SubscribeDlcEvents(DLCs);
            _allDlcEnabled = enabled;
            RaisePropertyChanged(() => AllDlcEnabled);
            // Reassign to force DataGrid checkboxes to redraw
            DLCs = new ObservableCollection<DlcApp>(DLCs);
        }

        // -----------------------------------------------------------------------
        // Properties — Achievements
        // -----------------------------------------------------------------------

        public ObservableCollection<Achievement> Achievements
        {
            get => _achievements;
            set
            {
                UnsubscribeAchievementEvents(_achievements);
                _achievements = value;
                SubscribeAchievementEvents(_achievements);
                RaisePropertyChanged(() => Achievements);
                UpdateAllAchievementsUnlockedState();
            }
        }

        public bool? AllAchievementsUnlocked
        {
            get => _allAchievementsUnlocked;
            set
            {
                bool newValue = _allAchievementsUnlocked != true;
                _allAchievementsUnlocked = newValue;
                RaisePropertyChanged(() => AllAchievementsUnlocked);
                SetAllAchievementsUnlocked(newValue);
            }
        }

        private void SubscribeAchievementEvents(IEnumerable<Achievement> items)
        {
            if (items == null) return;
            foreach (var a in items) a.PropertyChanged += OnAchievementPropertyChanged;
        }

        private void UnsubscribeAchievementEvents(IEnumerable<Achievement> items)
        {
            if (items == null) return;
            foreach (var a in items) a.PropertyChanged -= OnAchievementPropertyChanged;
        }

        private void OnAchievementPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Achievement.Unlocked))
                UpdateAllAchievementsUnlockedState();
        }

        private void UpdateAllAchievementsUnlockedState()
        {
            _allAchievementsUnlocked = (_achievements == null || _achievements.Count == 0) ? false
                                     : _achievements.All(x => x.Unlocked)                 ? true
                                     : _achievements.All(x => !x.Unlocked)                ? false
                                     : (bool?)null; // mixed → indeterminate
            RaisePropertyChanged(() => AllAchievementsUnlocked);
        }

        private void SetAllAchievementsUnlocked(bool unlocked)
        {
            if (_achievements == null) return;
            UnsubscribeAchievementEvents(_achievements);
            foreach (var a in _achievements) a.Unlocked = unlocked;
            SubscribeAchievementEvents(_achievements);
            _allAchievementsUnlocked = unlocked;
            RaisePropertyChanged(() => AllAchievementsUnlocked);
            // Reassign to force DataGrid to redraw
            Achievements = new ObservableCollection<Achievement>(Achievements);
        }

        // -----------------------------------------------------------------------
        // Properties — global settings
        // -----------------------------------------------------------------------

        public string AccountName
        {
            get => _accountName;
            set { _accountName = value; RaisePropertyChanged(() => AccountName); }
        }

        public long SteamId
        {
            get => _steamId;
            set { _steamId = value; RaisePropertyChanged(() => SteamId); }
        }

        public bool Offline
        {
            get => _offline;
            set { _offline = value; RaisePropertyChanged(() => Offline); }
        }

        public bool DisableNetworking
        {
            get => _disableNetworking;
            set { _disableNetworking = value; RaisePropertyChanged(() => DisableNetworking); }
        }

        public bool DisableOverlay
        {
            get => _disableOverlay;
            set { _disableOverlay = value; RaisePropertyChanged(() => DisableOverlay); }
        }

        // -----------------------------------------------------------------------
        // Properties — UI state
        // -----------------------------------------------------------------------

        public bool MainWindowEnabled
        {
            get => _mainWindowEnabled;
            set { _mainWindowEnabled = value; RaisePropertyChanged(() => MainWindowEnabled); }
        }

        public bool GoldbergApplied
        {
            get => _goldbergApplied;
            set { _goldbergApplied = value; RaisePropertyChanged(() => GoldbergApplied); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; RaisePropertyChanged(() => StatusText); }
        }

        public ObservableCollection<string> SteamLanguages
        {
            get => _steamLanguages;
            set { _steamLanguages = value; RaisePropertyChanged(() => SteamLanguages); }
        }

        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set { _selectedLanguage = value; RaisePropertyChanged(() => SelectedLanguage); }
        }

        public ObservableCollection<string> Themes
        {
            get => _themes;
            set { _themes = value; RaisePropertyChanged(() => Themes); }
        }

        public string SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                _selectedTheme = value;
                RaisePropertyChanged(() => SelectedTheme);
                if (value != null) _theme.ApplyTheme(value);
            }
        }

        /// <summary>True when the steam_interfaces.txt has NOT yet been generated (button should be enabled).</summary>
        public bool SteamInterfacesTxtExists
        {
            get
            {
                var dllPathDirExists = GetDllPathDir(out var dirPath);
                return dllPathDirExists && !File.Exists(Path.Combine(dirPath, "steam_interfaces.txt"));
            }
        }

        /// <summary>True once the user has selected a real DLL path.</summary>
        public bool DllSelected
        {
            get
            {
                var value = !DllPath.Contains(DllPathPlaceholder);
                if (!value) _log.Warn("No DLL selected! Skipping...");
                return value;
            }
        }

        public static string AboutVersionText =>
            FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;

        public static GlobalHelp G => new GlobalHelp();

        // -----------------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------------

        public IMvxCommand OpenFileCommand           => new MvxAsyncCommand(OpenFile);
        public IMvxCommand FindIdCommand             => new MvxAsyncCommand(FindId);
        public IMvxCommand GetListOfAchievementsCommand => new MvxAsyncCommand(GetListOfAchievements);
        public IMvxCommand GetListOfDlcCommand       => new MvxAsyncCommand(GetListOfDlc);
        public IMvxCommand SelectAllDlcCommand       => new MvxCommand(() => SetAllDlcEnabled(true));
        public IMvxCommand DeselectAllDlcCommand     => new MvxCommand(() => SetAllDlcEnabled(false));
        public IMvxCommand SaveConfigCommand         => new MvxAsyncCommand(SaveConfig);
        public IMvxCommand RevertCommand             => new MvxAsyncCommand(RevertConfig);
        public IMvxCommand GenerateSteamInterfacesCommand => new MvxAsyncCommand(GenerateSteamInterfaces);
        public IMvxCommand PasteDlcCommand           => new MvxCommand(PasteDlc);
        public IMvxCommand OpenGlobalSettingsFolderCommand => new MvxCommand(OpenGlobalSettingsFolder);

        // -----------------------------------------------------------------------
        // Command implementations
        // -----------------------------------------------------------------------

        private async Task OpenFile()
        {
            MainWindowEnabled = false;
            StatusText = "Please choose a file...";

            var dialog = new OpenFileDialog
            {
                Filter = "SteamAPI DLL|steam_api.dll;steam_api64.dll|All files (*.*)|*.*",
                Multiselect = false,
                Title = "Select SteamAPI DLL..."
            };

            if (dialog.ShowDialog() != true)
            {
                MainWindowEnabled = true;
                _log.Warn("File selection canceled.");
                StatusText = "No file selected! Ready.";
                return;
            }

            DllPath = dialog.FileName;
            await ReadConfig().ConfigureAwait(false);
            if (!GoldbergApplied) await GetListOfDlc().ConfigureAwait(false);
            MainWindowEnabled = true;
            StatusText = "Ready.";
        }

        private async Task FindId()
        {
            if (GameName.Contains(GameNamePlaceholder))
            {
                _log.Error("No game name entered!");
                return;
            }

            MainWindowEnabled = false;
            StatusText = "Trying to find AppID...";

            var appByName = await _steam.GetAppByName(_gameName).ConfigureAwait(false);
            if (appByName != null)
            {
                GameName = appByName.Name;
                AppId = appByName.AppId;
            }
            else
            {
                var list = await _steam.GetListOfAppsByName(GameName).ConfigureAwait(false);
                var steamApps = list as SteamApp[] ?? list.ToArray();

                // If exactly one result and it's valid, use it directly; otherwise show the picker
                if (steamApps.Length == 1 && steamApps[0] != null)
                {
                    GameName = steamApps[0].Name;
                    AppId = steamApps[0].AppId;
                }
                else
                {
                    await ShowSearchResultPicker(steamApps).ConfigureAwait(false);
                }
            }

            await GetListOfDlc().ConfigureAwait(false);
            MainWindowEnabled = true;
            StatusText = "Ready.";
        }

        private async Task ShowSearchResultPicker(SteamApp[] steamApps)
        {
            var result = await _navigationService
                .Navigate<SearchResultViewModel, IEnumerable<SteamApp>, SteamApp>(steamApps)
                .ConfigureAwait(false);
            if (result != null)
            {
                GameName = result.Name;
                AppId = result.AppId;
            }
        }

        private async Task GetNameById()
        {
            if (AppId <= 0)
            {
                _log.Error("Invalid Steam App!");
                return;
            }

            var steamApp = await _steam.GetAppById(AppId).ConfigureAwait(false);
            if (steamApp != null) GameName = steamApp.Name;
        }

        private async Task GetListOfAchievements()
        {
            if (AppId <= 0)
            {
                _log.Error("Invalid Steam App!");
                return;
            }

            MainWindowEnabled = false;
            StatusText = "Trying to get list of achievements...";
            var list = await _steam.GetListOfAchievements(new SteamApp { AppId = AppId, Name = GameName });
            Achievements = new MvxObservableCollection<Achievement>(list);
            MainWindowEnabled = true;

            StatusText = Achievements.Count > 0
                ? $"Successfully got {Achievements.Count} achievement{(Achievements.Count == 1 ? "" : "s")}! Ready."
                : "No achievements found! Ready.";
        }

        private async Task GetListOfDlc()
        {
            if (AppId <= 0)
            {
                _log.Error("Invalid Steam App!");
                return;
            }

            MainWindowEnabled = false;
            StatusText = "Trying to get list of DLCs...";
            var list = await _steam.GetListOfDlc(new SteamApp { AppId = AppId, Name = GameName }, true)
                .ConfigureAwait(false);
            DLCs = new MvxObservableCollection<DlcApp>(list);
            MainWindowEnabled = true;

            StatusText = DLCs.Count > 0
                ? $"Successfully got {DLCs.Count} DLC{(DLCs.Count == 1 ? "" : "s")}! Ready."
                : "No DLC found! Ready.";
        }

        private async Task SaveConfig()
        {
            _log.Info("Saving global settings...");
            await _goldberg.SetGlobalSettings(new GoldbergGlobalConfiguration
            {
                AccountName = AccountName,
                UserSteamId = SteamId,
                Language = SelectedLanguage
            }).ConfigureAwait(false);

            if (!DllSelected) return;
            if (!GetDllPathDir(out var dirPath)) return;

            _log.Info("Saving Goldberg settings...");
            MainWindowEnabled = false;
            StatusText = "Saving...";
            await _goldberg.Save(dirPath, new GoldbergConfiguration
            {
                AppId = AppId,
                Achievements = Achievements.ToList(),
                DlcList = DLCs.ToList(),
                Offline = Offline,
                DisableNetworking = DisableNetworking,
                DisableOverlay = DisableOverlay
            }).ConfigureAwait(false);

            GoldbergApplied = _goldberg.GoldbergApplied(dirPath);
            MainWindowEnabled = true;
            StatusText = "Ready.";
        }

        private async Task RevertConfig()
        {
            if (!DllSelected)
            {
                StatusText = "No DLL selected! Ready.";
                return;
            }

            if (!GetDllPathDir(out var dirPath)) return;

            var confirm = MessageBox.Show(
                "This will remove all Goldberg files and restore the original Steam API DLL.\n\nAre you sure?",
                "Revert Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            MainWindowEnabled = false;
            StatusText = "Reverting...";
            await _goldberg.Revert(dirPath).ConfigureAwait(false);
            GoldbergApplied = _goldberg.GoldbergApplied(dirPath);

            AppId = -1;
            Achievements = new ObservableCollection<Achievement>();
            DLCs = new ObservableCollection<DlcApp>();
            Offline = false;
            DisableNetworking = false;
            DisableOverlay = false;

            MainWindowEnabled = true;
            StatusText = "Reverted successfully! Ready.";
        }

        private async Task GenerateSteamInterfaces()
        {
            if (!DllSelected) return;

            _log.Info("Generate steam_interfaces.txt...");
            MainWindowEnabled = false;
            StatusText = @"Generating ""steam_interfaces.txt"".";

            GetDllPathDir(out var dirPath);
            var originalDll =
                File.Exists(Path.Combine(dirPath, "steam_api_o.dll"))   ? Path.Combine(dirPath, "steam_api_o.dll") :
                File.Exists(Path.Combine(dirPath, "steam_api64_o.dll")) ? Path.Combine(dirPath, "steam_api64_o.dll") :
                DllPath;

            await _goldberg.GenerateInterfacesFile(originalDll).ConfigureAwait(false);
            await RaisePropertyChanged(() => SteamInterfacesTxtExists).ConfigureAwait(false);
            MainWindowEnabled = true;
            StatusText = "Ready.";
        }

        private void PasteDlc()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            _log.Info("Trying to paste DLC list...");
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText) && !Clipboard.ContainsText(TextDataFormat.Text))
            {
                _log.Warn("Invalid DLC list!");
                return;
            }

            var expression = new Regex(@"(?<id>.*) *= *(?<n>.*)");
            var pastedDlc = Clipboard.GetText()
                .Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => expression.Match(line))
                .Where(m => m.Success)
                .Select(m => new DlcApp
                {
                    AppId = Convert.ToInt32(m.Groups["id"].Value),
                    Name  = m.Groups["n"].Value
                })
                .ToList();

            if (pastedDlc.Count > 0)
            {
                DLCs = new ObservableCollection<DlcApp>(pastedDlc);
                StatusText = pastedDlc.Count == 1
                    ? "Successfully got one DLC from clipboard! Ready."
                    : $"Successfully got {pastedDlc.Count} DLCs from clipboard! Ready.";
            }
            else
            {
                StatusText = "No DLC found in clipboard! Ready.";
            }
        }

        private void OpenGlobalSettingsFolder()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                StatusText = "Can't open folder (Windows only)! Ready.";
                return;
            }

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GSE Saves", "settings");
            Process.Start("explorer.exe", path)?.Dispose();
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        private void ResetForm()
        {
            DllPath = DllPathPlaceholder;
            GameName = GameNamePlaceholder;
            AppId = -1;
            Achievements = new ObservableCollection<Achievement>();
            DLCs = new ObservableCollection<DlcApp>();
            AccountName = "Account name...";
            SteamId = -1;
            Offline = false;
            DisableNetworking = false;
            DisableOverlay = false;
        }

        private async Task ReadConfig()
        {
            if (!GetDllPathDir(out var dirPath)) return;
            var config = await _goldberg.Read(dirPath).ConfigureAwait(false);
            SetFormFromConfig(config);
            GoldbergApplied = _goldberg.GoldbergApplied(dirPath);
            await RaisePropertyChanged(() => SteamInterfacesTxtExists).ConfigureAwait(false);
        }

        private void SetFormFromConfig(GoldbergConfiguration config)
        {
            AppId = config.AppId;
            Achievements = new ObservableCollection<Achievement>(config.Achievements);
            DLCs = new ObservableCollection<DlcApp>(config.DlcList);
            Offline = config.Offline;
            DisableNetworking = config.DisableNetworking;
            DisableOverlay = config.DisableOverlay;
        }

        private bool GetDllPathDir(out string dirPath)
        {
            if (!DllSelected)
            {
                dirPath = null;
                return false;
            }

            dirPath = Path.GetDirectoryName(DllPath);
            if (dirPath != null) return true;

            _log.Error($"Invalid directory for {DllPath}.");
            return false;
        }
    }
}
