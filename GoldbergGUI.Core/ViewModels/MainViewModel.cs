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
        private bool _runeApplied;
        private bool _ali213Applied;
        private bool _steamclientModeApplied;
        private bool _useSteamclientMode;
        private bool _useRuneMode;
        private bool _useAli213Mode;
        private List<string> _customBroadcastIps = new List<string>();
        private string _steamclientGameDir; // folder where loader/ini were written (may differ from DLL dir)
        private bool _globalSteamclientPreference; // persisted preference, never overwritten by game switches
        private bool _downloadAchievementImages = true;
        private ObservableCollection<string> _steamLanguages;
        private string _selectedLanguage;
        private ObservableCollection<string> _themes;
        private string _selectedTheme;
        private ObservableCollection<WorkshopMod> _workshopMods;
        private bool? _allModsEnabled = true;
        private ObservableCollection<ControllerActionSet> _controllerActionSets;
        private ControllerActionSet _selectedControllerActionSet;
        private string _controllerTemplateName;
        // Command backing fields — all cached so bindings always get the same instance
        private IMvxCommand _openFileCommand;
        private IMvxCommand _findIdCommand;
        private IMvxCommand _getListOfAchievementsCommand;
        private IMvxCommand _getListOfDlcCommand;
        private IMvxCommand _selectAllDlcCommand;
        private IMvxCommand _deselectAllDlcCommand;
        private IMvxCommand _saveConfigCommand;
        private IMvxCommand _revertCommand;
        private IMvxCommand _generateSteamInterfacesCommand;
        private IMvxCommand _pasteDlcCommand;
        private IMvxCommand _openGlobalSettingsFolderCommand;
        private IMvxCommand _addModFolderCommand;
        private IMvxCommand _removeWorkshopModCommand;
        private IMvxCommand _moveModUpCommand;
        private IMvxCommand _moveModDownCommand;
        private IMvxCommand _addControllerActionSetCommand;
        private IMvxCommand _removeControllerActionSetCommand;
        private IMvxCommand _addControllerBindingCommand;
        private IMvxCommand _removeControllerBindingCommand;
        private IMvxCommand _fetchControllerConfigCommand;
        private IMvxCommand _fetchControllerByFileIdCommand;
        private string _controllerVdfFileId = string.Empty;
        // RUNE controller settings
        private bool _runeControllerEnabled = true;
        private bool _runeControllerRumble = true;
        private bool _runeControllerSwapFaceButtons = false;
        private bool _runeControllerRawInput = false;
        private string _runeControllerForceController = string.Empty;
        private string _runeControllerGlyphsFolder = "rune_controller_glyphs";
        private int _runeControllerLeftJoystickDeadzone = 10000;
        private int _runeControllerRightJoystickDeadzone = 10000;
        private int _runeControllerLeftTriggerDeadzone = 26000;
        private int _runeControllerRightTriggerDeadzone = 26000;
        // ALI213 settings
        private int  _ali213SaveType          = 0;
        private bool _ali213Online            = false;
        private int  _ali213AchievementsCount = 0;
        private bool _ali213IsLoggedOn        = false;
        private bool _ali213FullBlockNetwork  = false;
        private bool _ali213FileRedirectCheck = false;
        private int  _ali213DecryptSteamStub  = 1;

        private static readonly Regex PasteDlcRegex = new Regex(@"(?<id>.*) *= *(?<n>.*)");

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
                    CustomBroadcastIps = string.Join(Environment.NewLine, globalConfig.CustomBroadcastIps ?? new List<string>());
                    _globalSteamclientPreference = globalConfig.UseSteamclientMode;
                    // Set backing field directly on init to avoid triggering auto-save
                    _useSteamclientMode = _globalSteamclientPreference;
                    RaisePropertyChanged(() => UseSteamclientMode);
                    _downloadAchievementImages = globalConfig.DownloadAchievementImages;
                    RaisePropertyChanged(() => DownloadAchievementImages);
                }
                catch (Exception e)
                {
                    _log.Error(e.Message);
                }
                finally
                {
                    MainWindowEnabled = true;
                    StatusText = "Ready.";
                }
            });
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

        public bool DownloadAchievementImages
        {
            get => _downloadAchievementImages;
            set { _downloadAchievementImages = value; RaisePropertyChanged(() => DownloadAchievementImages); }
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

        public bool SteamclientModeApplied
        {
            get => _steamclientModeApplied;
            set { _steamclientModeApplied = value; RaisePropertyChanged(() => SteamclientModeApplied); }
        }

        public bool RuneApplied
        {
            get => _runeApplied;
            set { _runeApplied = value; RaisePropertyChanged(() => RuneApplied); }
        }

        public bool Ali213Applied
        {
            get => _ali213Applied;
            set { _ali213Applied = value; RaisePropertyChanged(() => Ali213Applied); }
        }

        public bool UseRuneMode
        {
            get => _useRuneMode;
            set
            {
                if (_useRuneMode == value) return;
                _useRuneMode = value;
                if (value) _useAli213Mode = false;
                RaisePropertyChanged(() => UseRuneMode);
                RaisePropertyChanged(() => UseAli213Mode);
                RaisePropertyChanged(() => UseGbeMode);
                RaisePropertyChanged(() => NonAli213TabsVisible);
                // Steamclient mode is incompatible with RUNE
                if (value && _useSteamclientMode)
                {
                    _useSteamclientMode = false;
                    RaisePropertyChanged(() => UseSteamclientMode);
                }
            }
        }

        public bool UseAli213Mode
        {
            get => _useAli213Mode;
            set
            {
                if (_useAli213Mode == value) return;
                _useAli213Mode = value;
                if (value) _useRuneMode = false;
                RaisePropertyChanged(() => UseAli213Mode);
                RaisePropertyChanged(() => UseRuneMode);
                RaisePropertyChanged(() => UseGbeMode);
                RaisePropertyChanged(() => NonAli213TabsVisible);
                // Steamclient mode is incompatible with ALI213
                if (value && _useSteamclientMode)
                {
                    _useSteamclientMode = false;
                    RaisePropertyChanged(() => UseSteamclientMode);
                }
            }
        }

        /// <summary>True when not in ALI213 mode — used to hide Achievements and Controller tabs.</summary>
        public bool NonAli213TabsVisible => !_useAli213Mode;

        public bool UseGbeMode
        {
            get => !_useRuneMode && !_useAli213Mode;
            set
            {
                if (!value) return;
                if (!_useRuneMode && !_useAli213Mode) return;
                _useRuneMode = false;
                _useAli213Mode = false;
                RaisePropertyChanged(() => UseRuneMode);
                RaisePropertyChanged(() => UseAli213Mode);
                RaisePropertyChanged(() => UseGbeMode);
                RaisePropertyChanged(() => NonAli213TabsVisible);
            }
        }

        public bool UseSteamclientMode
        {
            get => _useSteamclientMode;
            set
            {
                if (_useSteamclientMode == value) return;
                _useSteamclientMode = value;
                _globalSteamclientPreference = value;
                RaisePropertyChanged(() => UseSteamclientMode);
                // Persist immediately so closing without pressing Save still remembers the choice
                _ = PersistGlobalSteamclientPreference();
            }
        }

        public string CustomBroadcastIps
        {
            get => string.Join(Environment.NewLine, _customBroadcastIps);
            set
            {
                _customBroadcastIps = (value ?? "")
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                RaisePropertyChanged(() => CustomBroadcastIps);
            }
        }

        private Task PersistGlobalSteamclientPreference() =>
            _goldberg.SetGlobalSettings(new GoldbergGlobalConfiguration
            {
                AccountName               = AccountName,
                UserSteamId               = SteamId,
                Language                  = SelectedLanguage,
                CustomBroadcastIps        = _customBroadcastIps,
                UseSteamclientMode        = _globalSteamclientPreference,
                DownloadAchievementImages = _downloadAchievementImages,
            });

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

        // -----------------------------------------------------------------------
        // Properties — Workshop Mods
        // -----------------------------------------------------------------------

        public ObservableCollection<WorkshopMod> WorkshopMods
        {
            get => _workshopMods;
            set
            {
                UnsubscribeModEvents(_workshopMods);
                _workshopMods = value;
                SubscribeModEvents(_workshopMods);
                RaisePropertyChanged(() => WorkshopMods);
                UpdateAllModsEnabledState();
            }
        }

        public bool? AllModsEnabled
        {
            get => _allModsEnabled;
            set
            {
                bool newValue = _allModsEnabled != true;
                _allModsEnabled = newValue;
                RaisePropertyChanged(() => AllModsEnabled);
                SetAllModsEnabled(newValue);
            }
        }

        private void SubscribeModEvents(IEnumerable<WorkshopMod> items)
        {
            if (items == null) return;
            foreach (var m in items) m.PropertyChanged += OnModPropertyChanged;
        }

        private void UnsubscribeModEvents(IEnumerable<WorkshopMod> items)
        {
            if (items == null) return;
            foreach (var m in items) m.PropertyChanged -= OnModPropertyChanged;
        }

        private void OnModPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorkshopMod.Enabled))
                UpdateAllModsEnabledState();
        }

        private void UpdateAllModsEnabledState()
        {
            _allModsEnabled = (_workshopMods == null || _workshopMods.Count == 0) ? true
                            : _workshopMods.All(x => x.Enabled)                   ? true
                            : _workshopMods.All(x => !x.Enabled)                  ? false
                            : (bool?)null;
            RaisePropertyChanged(() => AllModsEnabled);
        }

        private void SetAllModsEnabled(bool enabled)
        {
            if (_workshopMods == null) return;
            UnsubscribeModEvents(_workshopMods);
            foreach (var m in _workshopMods) m.Enabled = enabled;
            SubscribeModEvents(_workshopMods);
            _allModsEnabled = enabled;
            RaisePropertyChanged(() => AllModsEnabled);
            WorkshopMods = new ObservableCollection<WorkshopMod>(_workshopMods);
        }

        // -----------------------------------------------------------------------
        // Properties — Controller
        // -----------------------------------------------------------------------

        public ObservableCollection<ControllerActionSet> ControllerActionSets
        {
            get => _controllerActionSets;
            set { _controllerActionSets = value; RaisePropertyChanged(() => ControllerActionSets); }
        }

        public ControllerActionSet SelectedControllerActionSet
        {
            get => _selectedControllerActionSet;
            set
            {
                _selectedControllerActionSet = value;
                RaisePropertyChanged(() => SelectedControllerActionSet);
                RaisePropertyChanged(() => ControllerActionSetSelected);
            }
        }

        public bool ControllerActionSetSelected => _selectedControllerActionSet != null;

        /// <summary>Manual file-ID entry for the Steam VDF fallback (e.g. from SteamDB).</summary>
        public string ControllerVdfFileId
        {
            get => _controllerVdfFileId;
            set { _controllerVdfFileId = value ?? string.Empty; RaisePropertyChanged(() => ControllerVdfFileId); }
        }

        /// <summary>
        /// Human-readable name of the Steam controller template detected for the current app
        /// (e.g. "Gamepad + Keyboard (Mouse)"). Null/empty when no template was found.
        /// </summary>
        public string ControllerTemplateName => _controllerTemplateName;

        /// <summary>True when a Steam controller template was detected (shows the info banner).</summary>
        public bool ControllerTemplateVisible => !string.IsNullOrEmpty(_controllerTemplateName);

        // -----------------------------------------------------------------------
        // Properties — RUNE Controller Settings
        // -----------------------------------------------------------------------

        public static IReadOnlyList<string> RuneControllerForceOptions { get; } =
            new[] { "", "Xbox360", "XboxOne", "PS3", "PS4", "PS5", "SwitchPro", "Generic" };

        public bool RuneControllerEnabled
        {
            get => _runeControllerEnabled;
            set { _runeControllerEnabled = value; RaisePropertyChanged(() => RuneControllerEnabled); }
        }

        public bool RuneControllerRumble
        {
            get => _runeControllerRumble;
            set { _runeControllerRumble = value; RaisePropertyChanged(() => RuneControllerRumble); }
        }

        public bool RuneControllerSwapFaceButtons
        {
            get => _runeControllerSwapFaceButtons;
            set { _runeControllerSwapFaceButtons = value; RaisePropertyChanged(() => RuneControllerSwapFaceButtons); }
        }

        public bool RuneControllerRawInput
        {
            get => _runeControllerRawInput;
            set { _runeControllerRawInput = value; RaisePropertyChanged(() => RuneControllerRawInput); }
        }

        public string RuneControllerForceController
        {
            get => _runeControllerForceController;
            set { _runeControllerForceController = value ?? string.Empty; RaisePropertyChanged(() => RuneControllerForceController); }
        }

        public string RuneControllerGlyphsFolder
        {
            get => _runeControllerGlyphsFolder;
            set { _runeControllerGlyphsFolder = value ?? "rune_controller_glyphs"; RaisePropertyChanged(() => RuneControllerGlyphsFolder); }
        }

        public int RuneControllerLeftJoystickDeadzone
        {
            get => _runeControllerLeftJoystickDeadzone;
            set { _runeControllerLeftJoystickDeadzone = value; RaisePropertyChanged(() => RuneControllerLeftJoystickDeadzone); }
        }

        public int RuneControllerRightJoystickDeadzone
        {
            get => _runeControllerRightJoystickDeadzone;
            set { _runeControllerRightJoystickDeadzone = value; RaisePropertyChanged(() => RuneControllerRightJoystickDeadzone); }
        }

        public int RuneControllerLeftTriggerDeadzone
        {
            get => _runeControllerLeftTriggerDeadzone;
            set { _runeControllerLeftTriggerDeadzone = value; RaisePropertyChanged(() => RuneControllerLeftTriggerDeadzone); }
        }

        public int RuneControllerRightTriggerDeadzone
        {
            get => _runeControllerRightTriggerDeadzone;
            set { _runeControllerRightTriggerDeadzone = value; RaisePropertyChanged(() => RuneControllerRightTriggerDeadzone); }
        }

        // -----------------------------------------------------------------------
        // Properties — ALI213 Settings
        // -----------------------------------------------------------------------

        public record Ali213Option(int Value, string Display);

        public static IReadOnlyList<Ali213Option> Ali213SaveTypeOptions { get; } = new Ali213Option[]
        {
            new(0, "0 – VALVE (game dir)"),
            new(1, "1 – VALVE (My Documents)"),
            new(4, "4 – RELOADED"),
            new(5, "5 – SKIDROW"),
            new(6, "6 – FLT"),
            new(7, "7 – CODEX (3.0.4+ / My Documents)"),
            new(8, "8 – CODEX (1.0.0.0+ / AppData)"),
        };

        public static IReadOnlyList<Ali213Option> Ali213DecryptOptions { get; } = new Ali213Option[]
        {
            new(0, "0 – Disabled"),
            new(1, "1 – steam_api(64).dll decrypts stub"),
            new(2, "2 – SteamClient.dll decrypts stub"),
        };

        public int Ali213SaveType
        {
            get => _ali213SaveType;
            set { _ali213SaveType = value; RaisePropertyChanged(() => Ali213SaveType); }
        }

        public bool Ali213Online
        {
            get => _ali213Online;
            set { _ali213Online = value; RaisePropertyChanged(() => Ali213Online); }
        }

        public int Ali213AchievementsCount
        {
            get => _ali213AchievementsCount;
            set { _ali213AchievementsCount = value; RaisePropertyChanged(() => Ali213AchievementsCount); }
        }

        public bool Ali213IsLoggedOn
        {
            get => _ali213IsLoggedOn;
            set { _ali213IsLoggedOn = value; RaisePropertyChanged(() => Ali213IsLoggedOn); }
        }

        public bool Ali213FullBlockNetwork
        {
            get => _ali213FullBlockNetwork;
            set { _ali213FullBlockNetwork = value; RaisePropertyChanged(() => Ali213FullBlockNetwork); }
        }

        public bool Ali213FileRedirectCheck
        {
            get => _ali213FileRedirectCheck;
            set { _ali213FileRedirectCheck = value; RaisePropertyChanged(() => Ali213FileRedirectCheck); }
        }

        public int Ali213DecryptSteamStub
        {
            get => _ali213DecryptSteamStub;
            set { _ali213DecryptSteamStub = value; RaisePropertyChanged(() => Ali213DecryptSteamStub); }
        }

        public bool SteamInterfacesTxtExists => DllSelected;

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
            FileVersionInfo.GetVersionInfo(
                Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location).FileVersion;

        private static readonly GlobalHelp _globalHelp = new GlobalHelp();
        public static GlobalHelp G => _globalHelp;

        // -----------------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------------

        public IMvxCommand OpenFileCommand                => _openFileCommand                ??= new MvxAsyncCommand(OpenFile);
        public IMvxCommand FindIdCommand                  => _findIdCommand                  ??= new MvxAsyncCommand(FindId);
        public IMvxCommand GetListOfAchievementsCommand   => _getListOfAchievementsCommand   ??= new MvxAsyncCommand(GetListOfAchievements);
        public IMvxCommand GetListOfDlcCommand            => _getListOfDlcCommand            ??= new MvxAsyncCommand(GetListOfDlc);
        public IMvxCommand SelectAllDlcCommand            => _selectAllDlcCommand            ??= new MvxCommand(() => SetAllDlcEnabled(true));
        public IMvxCommand DeselectAllDlcCommand          => _deselectAllDlcCommand          ??= new MvxCommand(() => SetAllDlcEnabled(false));
        public IMvxCommand SaveConfigCommand              => _saveConfigCommand              ??= new MvxAsyncCommand(SaveConfig);
        public IMvxCommand RevertCommand                  => _revertCommand                  ??= new MvxAsyncCommand(RevertConfig);
        public IMvxCommand GenerateSteamInterfacesCommand => _generateSteamInterfacesCommand ??= new MvxAsyncCommand(GenerateSteamInterfaces);
        public IMvxCommand PasteDlcCommand                => _pasteDlcCommand                ??= new MvxCommand(PasteDlc);
        public IMvxCommand OpenGlobalSettingsFolderCommand => _openGlobalSettingsFolderCommand ??= new MvxCommand(OpenGlobalSettingsFolder);
        public IMvxCommand AddModFolderCommand             => _addModFolderCommand            ??= new MvxAsyncCommand(AddModFolder);
        public IMvxCommand RemoveWorkshopModCommand        => _removeWorkshopModCommand       ??= new MvxAsyncCommand<WorkshopMod>(RemoveWorkshopMod);
        public IMvxCommand MoveModUpCommand                => _moveModUpCommand               ??= new MvxCommand<WorkshopMod>(MoveModUp);
        public IMvxCommand MoveModDownCommand              => _moveModDownCommand             ??= new MvxCommand<WorkshopMod>(MoveModDown);
        public IMvxCommand AddControllerActionSetCommand     => _addControllerActionSetCommand     ??= new MvxCommand(AddControllerActionSet);
        public IMvxCommand RemoveControllerActionSetCommand  => _removeControllerActionSetCommand  ??= new MvxCommand(RemoveControllerActionSet);
        public IMvxCommand AddControllerBindingCommand       => _addControllerBindingCommand       ??= new MvxCommand(AddControllerBinding);
        public IMvxCommand RemoveControllerBindingCommand    => _removeControllerBindingCommand    ??= new MvxCommand<ControllerBinding>(RemoveControllerBinding);
        public IMvxCommand FetchControllerConfigCommand      => _fetchControllerConfigCommand      ??= new MvxAsyncCommand(FetchControllerConfig);
        public IMvxCommand FetchControllerByFileIdCommand    => _fetchControllerByFileIdCommand    ??= new MvxAsyncCommand(FetchControllerByFileId);

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
            _steamclientGameDir = null; // clear cached gameDir so ReadConfig searches fresh
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
            // Persist whatever the user currently has checked as the global preference
            _globalSteamclientPreference = UseSteamclientMode;
            await _goldberg.SetGlobalSettings(new GoldbergGlobalConfiguration
            {
                AccountName               = AccountName,
                UserSteamId               = SteamId,
                Language                  = SelectedLanguage,
                CustomBroadcastIps        = _customBroadcastIps,
                UseSteamclientMode        = _globalSteamclientPreference,
                DownloadAchievementImages = _downloadAchievementImages,
            }).ConfigureAwait(false);

            if (!DllSelected)
            {
                StatusText = "Global settings saved! Ready.";
                return;
            }
            if (!GetDllPathDir(out var dirPath)) return;

            _log.Info("Saving Goldberg settings...");
            MainWindowEnabled = false;
            StatusText = "Saving...";

            var config = new GoldbergConfiguration
            {
                AppId                            = AppId,
                Achievements                     = Achievements.ToList(),
                DlcList                          = DLCs.ToList(),
                WorkshopMods                     = WorkshopMods.ToList(),
                ControllerActionSets             = ControllerActionSets.ToList(),
                Offline                          = Offline,
                DisableNetworking                = DisableNetworking,
                DisableOverlay                   = DisableOverlay,
                Ali213SaveType          = Ali213SaveType,
                Ali213Online            = Ali213Online,
                Ali213AchievementsCount = Ali213AchievementsCount,
                Ali213IsLoggedOn        = Ali213IsLoggedOn,
                Ali213FullBlockNetwork  = Ali213FullBlockNetwork,
                Ali213FileRedirectCheck = Ali213FileRedirectCheck,
                Ali213DecryptSteamStub  = Ali213DecryptSteamStub,
                RuneControllerEnabled            = RuneControllerEnabled,
                RuneControllerRumble             = RuneControllerRumble,
                RuneControllerSwapFaceButtons    = RuneControllerSwapFaceButtons,
                RuneControllerRawInput           = RuneControllerRawInput,
                RuneControllerForceController    = RuneControllerForceController,
                RuneControllerGlyphsFolder       = RuneControllerGlyphsFolder,
                RuneControllerLeftJoystickDeadzone  = RuneControllerLeftJoystickDeadzone,
                RuneControllerRightJoystickDeadzone = RuneControllerRightJoystickDeadzone,
                RuneControllerLeftTriggerDeadzone   = RuneControllerLeftTriggerDeadzone,
                RuneControllerRightTriggerDeadzone  = RuneControllerRightTriggerDeadzone,
            };

            // Fetch supplementary Steam data in parallel (stats, branches, supported languages)
            if (AppId > 0)
            {
                StatusText = "Fetching supplementary data from Steam...";
                var statsTask     = _steam.GetStats(AppId);
                var branchesTask  = _steam.GetBranchesJson(AppId);
                var languagesTask = _steam.GetSupportedLanguages(AppId);
                await Task.WhenAll(statsTask, branchesTask, languagesTask).ConfigureAwait(false);
                config.Stats              = statsTask.Result;
                config.BranchesJson       = branchesTask.Result;
                config.SupportedLanguages = languagesTask.Result;
            }

            if (UseRuneMode)
            {
                // RUNE mode: clean up gbe_fork and ALI213 if present, then apply RUNE
                if (_goldberg.GoldbergApplied(dirPath))
                    await _goldberg.Revert(dirPath).ConfigureAwait(false);
                if (_goldberg.Ali213Applied(dirPath))
                    await _goldberg.RevertAli213(dirPath).ConfigureAwait(false);
                await _goldberg.RevertSteamclientMode(GetSteamclientGameDir(dirPath)).ConfigureAwait(false);
                _steamclientGameDir = null;
                await _goldberg.ApplyRune(dirPath, config, AccountName, SteamId, SelectedLanguage)
                    .ConfigureAwait(false);
            }
            else if (UseAli213Mode)
            {
                // ALI213 mode: clean up gbe_fork and RUNE if present, then apply ALI213
                if (_goldberg.GoldbergApplied(dirPath))
                    await _goldberg.Revert(dirPath).ConfigureAwait(false);
                if (_goldberg.RuneApplied(dirPath))
                    await _goldberg.RevertRune(dirPath).ConfigureAwait(false);
                await _goldberg.RevertSteamclientMode(GetSteamclientGameDir(dirPath)).ConfigureAwait(false);
                _steamclientGameDir = null;
                await _goldberg.ApplyAli213(dirPath, config, AccountName, SteamId, SelectedLanguage)
                    .ConfigureAwait(false);
            }
            else if (UseSteamclientMode)
            {
                // Clean up RUNE, ALI213, and normal gbe_fork if present before applying steamclient mode
                if (_goldberg.RuneApplied(dirPath))
                    await _goldberg.RevertRune(dirPath).ConfigureAwait(false);
                if (_goldberg.Ali213Applied(dirPath))
                    await _goldberg.RevertAli213(dirPath).ConfigureAwait(false);
                if (_goldberg.GoldbergApplied(dirPath))
                    await _goldberg.Revert(dirPath).ConfigureAwait(false);
                else
                    await _goldberg.RevertDllOnly(dirPath).ConfigureAwait(true);
                string gameDir;
                if (!_goldberg.SteamclientModeApplied(GetSteamclientGameDir(dirPath)))
                {
                    var dialog = new OpenFileDialog
                    {
                        Filter = "Executable|*.exe|All files (*.*)|*.*",
                        Title = "Select the game executable for the steamclient loader...",
                        InitialDirectory = dirPath
                    };
                    MainWindowEnabled = true;
                    if (dialog.ShowDialog() != true)
                    {
                        StatusText = "Steamclient setup cancelled. Ready.";
                        return;
                    }
                    MainWindowEnabled = false;
                    _steamclientGameDir = Path.GetDirectoryName(dialog.FileName) ?? dirPath;
                    gameDir = _steamclientGameDir;
                    await _goldberg.SetupSteamclientMode(gameDir, Path.GetFileName(dialog.FileName), AppId)
                        .ConfigureAwait(false);
                }
                else
                {
                    gameDir = GetSteamclientGameDir(dirPath);
                    _steamclientGameDir = gameDir;
                    await _goldberg.SetupSteamclientMode(gameDir,
                        _goldberg.GetSteamclientExeName(gameDir), AppId).ConfigureAwait(false);
                }
                // Config must be beside steamclient64.dll (game root), per gbe_fork README
                await _goldberg.SaveConfigOnly(gameDir, config).ConfigureAwait(true);
            }
            else
            {
                // Normal gbe_fork mode: clean up RUNE and ALI213 if present, then apply gbe_fork
                if (_goldberg.RuneApplied(dirPath))
                    await _goldberg.RevertRune(dirPath).ConfigureAwait(false);
                if (_goldberg.Ali213Applied(dirPath))
                    await _goldberg.RevertAli213(dirPath).ConfigureAwait(false);
                await _goldberg.RevertSteamclientMode(GetSteamclientGameDir(dirPath)).ConfigureAwait(false);
                _steamclientGameDir = null;
                await _goldberg.Save(dirPath, config).ConfigureAwait(false);
            }

            GoldbergApplied = _goldberg.GoldbergApplied(dirPath);
            RuneApplied = _goldberg.RuneApplied(dirPath);
            Ali213Applied = _goldberg.Ali213Applied(dirPath);
            SteamclientModeApplied = _goldberg.SteamclientModeApplied(GetSteamclientGameDir(dirPath));
            MainWindowEnabled = true;
            StatusText = UseRuneMode
                ? "Saved with RUNE! Launch via the game exe. Ready."
                : UseAli213Mode
                    ? "Saved with ALI213! Launch via the game exe. Ready."
                    : UseSteamclientMode
                        ? "Saved! Launch the game via steamclient_loader_x64.exe (or x32). Ready."
                        : "Ready.";
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
                "This will remove all gbe_fork and RUNE files and restore the original Steam API DLL.\n\nAre you sure?",
                "Revert Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            MainWindowEnabled = false;
            StatusText = "Reverting...";
            try
            {
                var steamclientGameDir = GetSteamclientGameDir(dirPath);
                await _goldberg.Revert(dirPath);
                if (!string.Equals(steamclientGameDir, dirPath, StringComparison.OrdinalIgnoreCase))
                    await _goldberg.RevertSteamclientMode(steamclientGameDir);
                // Always run RevertRune/RevertAli213 — they use DeleteIfExists so are safe even if nothing is there
                await _goldberg.RevertRune(dirPath);
                await _goldberg.RevertAli213(dirPath);
                _steamclientGameDir = null;
                GoldbergApplied = _goldberg.GoldbergApplied(dirPath);
                RuneApplied = _goldberg.RuneApplied(dirPath);
                Ali213Applied = _goldberg.Ali213Applied(dirPath);
                SteamclientModeApplied = _goldberg.SteamclientModeApplied(GetSteamclientGameDir(dirPath));

                AppId = -1;
                Achievements = new ObservableCollection<Achievement>();
                DLCs = new ObservableCollection<DlcApp>();
                WorkshopMods = new ObservableCollection<WorkshopMod>();
                ControllerActionSets = new ObservableCollection<ControllerActionSet>();
                SelectedControllerActionSet = null;
                _controllerTemplateName = null;
                RaisePropertyChanged(() => ControllerTemplateName);
                RaisePropertyChanged(() => ControllerTemplateVisible);
                Offline = false;
                DisableNetworking = false;
                DisableOverlay = false;
                ResetRuneControllerSettings();
                // Reset backing fields directly to avoid side-effects in setters.
                _useSteamclientMode = false;
                RaisePropertyChanged(() => UseSteamclientMode);
                _useRuneMode = false;
                _useAli213Mode = false;
                RaisePropertyChanged(() => UseRuneMode);
                RaisePropertyChanged(() => UseAli213Mode);
                RaisePropertyChanged(() => UseGbeMode);
                RaisePropertyChanged(() => NonAli213TabsVisible);
                StatusText = "Reverted successfully! Ready.";
            }
            catch (Exception ex)
            {
                StatusText = $"Revert failed: {ex.Message}  Ready.";
            }
            finally
            {
                MainWindowEnabled = true;
            }
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

            var pastedDlc = Clipboard.GetText()
                .Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => PasteDlcRegex.Match(line))
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
            WorkshopMods = new ObservableCollection<WorkshopMod>();
            ControllerActionSets = new ObservableCollection<ControllerActionSet>();
            SelectedControllerActionSet = null;
            _controllerTemplateName = null;
            RaisePropertyChanged(() => ControllerTemplateName);
            RaisePropertyChanged(() => ControllerTemplateVisible);
            AccountName = "Account name...";
            SteamId = -1;
            Offline = false;
            DisableNetworking = false;
            DisableOverlay = false;
            _ali213SaveType          = 0;
            _ali213Online            = false;
            _ali213AchievementsCount = 0;
            _ali213IsLoggedOn        = false;
            _ali213FullBlockNetwork  = false;
            _ali213FileRedirectCheck = false;
            _ali213DecryptSteamStub  = 1;
            RaisePropertyChanged(() => Ali213SaveType);
            RaisePropertyChanged(() => Ali213Online);
            RaisePropertyChanged(() => Ali213AchievementsCount);
            RaisePropertyChanged(() => Ali213IsLoggedOn);
            RaisePropertyChanged(() => Ali213FullBlockNetwork);
            RaisePropertyChanged(() => Ali213FileRedirectCheck);
            RaisePropertyChanged(() => Ali213DecryptSteamStub);
            ResetRuneControllerSettings();
        }

        private void ResetRuneControllerSettings()
        {
            _runeControllerEnabled            = true;
            _runeControllerRumble             = true;
            _runeControllerSwapFaceButtons    = false;
            _runeControllerRawInput           = false;
            _runeControllerForceController    = string.Empty;
            _runeControllerGlyphsFolder       = "rune_controller_glyphs";
            _runeControllerLeftJoystickDeadzone  = 10000;
            _runeControllerRightJoystickDeadzone = 10000;
            _runeControllerLeftTriggerDeadzone   = 26000;
            _runeControllerRightTriggerDeadzone  = 26000;
            RaisePropertyChanged(() => RuneControllerEnabled);
            RaisePropertyChanged(() => RuneControllerRumble);
            RaisePropertyChanged(() => RuneControllerSwapFaceButtons);
            RaisePropertyChanged(() => RuneControllerRawInput);
            RaisePropertyChanged(() => RuneControllerForceController);
            RaisePropertyChanged(() => RuneControllerGlyphsFolder);
            RaisePropertyChanged(() => RuneControllerLeftJoystickDeadzone);
            RaisePropertyChanged(() => RuneControllerRightJoystickDeadzone);
            RaisePropertyChanged(() => RuneControllerLeftTriggerDeadzone);
            RaisePropertyChanged(() => RuneControllerRightTriggerDeadzone);
        }

        private async Task ReadConfig()
        {
            if (!GetDllPathDir(out var dirPath)) return;
            var config = await _goldberg.Read(dirPath).ConfigureAwait(false);
            SetFormFromConfig(config);

            // Calculate sizes for loaded mods in the background, then apply on the UI thread.
            var mods = _workshopMods.ToList();
            var modsDir         = Path.Combine(dirPath, "steam_settings", "mods");
            var modsDisabledDir = Path.Combine(dirPath, "steam_settings", "mods_disabled");
            var sizes = await Task.Run(() => mods.ToDictionary(
                m => m.WorkshopId,
                m =>
                {
                    var active   = Path.Combine(modsDir,         m.WorkshopId.ToString());
                    var disabled = Path.Combine(modsDisabledDir, m.WorkshopId.ToString());
                    var folder   = Directory.Exists(active)   ? active
                                 : Directory.Exists(disabled) ? disabled
                                 : null;
                    return folder != null ? FormatFolderSize(folder) : string.Empty;
                }
            )).ConfigureAwait(true);
            foreach (var mod in mods)
                if (sizes.TryGetValue(mod.WorkshopId, out var size))
                    mod.SizeDisplay = size;

            GoldbergApplied = _goldberg.GoldbergApplied(dirPath);
            RuneApplied = _goldberg.RuneApplied(dirPath);
            Ali213Applied = _goldberg.Ali213Applied(dirPath);
            SteamclientModeApplied = _goldberg.SteamclientModeApplied(GetSteamclientGameDir(dirPath));
            // Auto-detect emulator mode from what is currently applied in the game directory
            if (_goldberg.RuneApplied(dirPath))
            {
                _useRuneMode = true;
                _useAli213Mode = false;
                RaisePropertyChanged(() => UseRuneMode);
                RaisePropertyChanged(() => UseAli213Mode);
                RaisePropertyChanged(() => UseGbeMode);
                RaisePropertyChanged(() => NonAli213TabsVisible);
            }
            else if (_goldberg.Ali213Applied(dirPath))
            {
                _useRuneMode = false;
                _useAli213Mode = true;
                RaisePropertyChanged(() => UseRuneMode);
                RaisePropertyChanged(() => UseAli213Mode);
                RaisePropertyChanged(() => UseGbeMode);
                RaisePropertyChanged(() => NonAli213TabsVisible);
            }
            else if (_goldberg.GoldbergApplied(dirPath) || _goldberg.SteamclientModeApplied(GetSteamclientGameDir(dirPath)))
            {
                _useRuneMode = false;
                _useAli213Mode = false;
                RaisePropertyChanged(() => UseRuneMode);
                RaisePropertyChanged(() => UseAli213Mode);
                RaisePropertyChanged(() => UseGbeMode);
                RaisePropertyChanged(() => NonAli213TabsVisible);
            }
            // UseSteamclientMode is intentionally NOT updated here — it is a global preference
            // that persists across game switches and app restarts, and is only changed by the
            // user manually toggling the Global Settings checkbox.
            await RaisePropertyChanged(() => SteamInterfacesTxtExists).ConfigureAwait(false);
        }

        private void SetFormFromConfig(GoldbergConfiguration config)
        {
            AppId = config.AppId;
            Achievements = new ObservableCollection<Achievement>(config.Achievements);
            DLCs = new ObservableCollection<DlcApp>(config.DlcList);
            WorkshopMods = new ObservableCollection<WorkshopMod>(config.WorkshopMods ?? new List<WorkshopMod>());
            ControllerActionSets = new ObservableCollection<ControllerActionSet>(
                config.ControllerActionSets ?? new List<ControllerActionSet>());
            SelectedControllerActionSet = ControllerActionSets.FirstOrDefault();
            // Template info is live-fetched only; never persisted to disk
            _controllerTemplateName = null;
            RaisePropertyChanged(() => ControllerTemplateName);
            RaisePropertyChanged(() => ControllerTemplateVisible);
            Offline = config.Offline;
            DisableNetworking = config.DisableNetworking;
            DisableOverlay = config.DisableOverlay;
            Ali213SaveType          = config.Ali213SaveType;
            Ali213Online            = config.Ali213Online;
            Ali213AchievementsCount = config.Ali213AchievementsCount;
            Ali213IsLoggedOn        = config.Ali213IsLoggedOn;
            Ali213FullBlockNetwork  = config.Ali213FullBlockNetwork;
            Ali213FileRedirectCheck = config.Ali213FileRedirectCheck;
            Ali213DecryptSteamStub  = config.Ali213DecryptSteamStub;
            RuneControllerEnabled            = config.RuneControllerEnabled;
            RuneControllerRumble             = config.RuneControllerRumble;
            RuneControllerSwapFaceButtons    = config.RuneControllerSwapFaceButtons;
            RuneControllerRawInput           = config.RuneControllerRawInput;
            RuneControllerForceController    = config.RuneControllerForceController;
            RuneControllerGlyphsFolder       = config.RuneControllerGlyphsFolder;
            RuneControllerLeftJoystickDeadzone  = config.RuneControllerLeftJoystickDeadzone;
            RuneControllerRightJoystickDeadzone = config.RuneControllerRightJoystickDeadzone;
            RuneControllerLeftTriggerDeadzone   = config.RuneControllerLeftTriggerDeadzone;
            RuneControllerRightTriggerDeadzone  = config.RuneControllerRightTriggerDeadzone;
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

        /// <summary>
        /// Returns the directory where steamclient loader files were written.
        /// Uses the cached _steamclientGameDir if set, otherwise searches for
        /// ColdClientLoader.ini near dirPath (same folder, then parent folder).
        /// Falls back to dirPath itself if not found.
        /// </summary>
        private string GetSteamclientGameDir(string dirPath)
        {
            if (!string.IsNullOrEmpty(_steamclientGameDir) && Directory.Exists(_steamclientGameDir))
                return _steamclientGameDir;

            // Check dirPath itself
            if (File.Exists(Path.Combine(dirPath, "ColdClientLoader.ini")))
                return dirPath;

            // Check one level up (e.g., dll in a direct subfolder of game root)
            var parent = Directory.GetParent(dirPath)?.FullName;
            if (parent != null && File.Exists(Path.Combine(parent, "ColdClientLoader.ini")))
                return parent;

            // Check two levels up (e.g., dll in GameData\Plugins\ under game root)
            var grandParent = parent != null ? Directory.GetParent(parent)?.FullName : null;
            if (grandParent != null && File.Exists(Path.Combine(grandParent, "ColdClientLoader.ini")))
                return grandParent;

            return dirPath;
        }

        // -----------------------------------------------------------------------
        // Workshop mod command implementations
        // -----------------------------------------------------------------------

        /// <summary>
        /// Adds a mod to the WorkshopMods collection, subscribes its property-change event,
        /// and updates the header checkbox state. Use this instead of WorkshopMods.Add() directly.
        /// </summary>
        private void AddModToList(WorkshopMod mod)
        {
            mod.PropertyChanged += OnModPropertyChanged;
            _workshopMods.Add(mod);
            UpdateAllModsEnabledState();
        }

        private async Task AddModFolder()
        {
            if (!DllSelected || !GetDllPathDir(out var dirPath))
            {
                StatusText = "Please select the game DLL before adding mods. Ready.";
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "Select a mod folder or a folder containing mod folders..."
            };

            if (dialog.ShowDialog() != true) return;

            var selectedFolder = dialog.FolderName;
            var folderName = Path.GetFileName(selectedFolder);

            // Single mod: folder name IS a numeric workshop ID
            // Container: folder contains direct subfolders with numeric names
            List<(long id, string path)> modsToAdd;

            if (long.TryParse(folderName, out var singleId) && singleId > 0)
            {
                modsToAdd = new List<(long, string)> { (singleId, selectedFolder) };
            }
            else
            {
                modsToAdd = new List<(long, string)>();
                foreach (var d in Directory.GetDirectories(selectedFolder))
                    if (long.TryParse(Path.GetFileName(d), out var id) && id > 0)
                        modsToAdd.Add((id, d));

                if (modsToAdd.Count == 0)
                {
                    StatusText = "Folder is not a mod (no numeric name) and contains no numeric-named mod subfolders. Ready.";
                    return;
                }
            }

            MainWindowEnabled = false;
            int added = 0, skipped = 0;

            foreach (var (workshopId, sourcePath) in modsToAdd)
            {
                if (WorkshopMods.Any(m => m.WorkshopId == workshopId))
                {
                    skipped++;
                    continue;
                }

                var targetDir = Path.Combine(dirPath, "steam_settings", "mods", workshopId.ToString());
                StatusText = $"Copying mod {workshopId}...";
                await Task.Run(() => CopyDirectory(sourcePath, targetDir)).ConfigureAwait(true);

                // Try to read the name from local metadata (e.g. descriptor.mod for Paradox games).
                // Fall back to Steam API if no local metadata is found.
                WorkshopMod mod;
                var localName = await Task.Run(() => TryReadModNameFromFolder(targetDir)).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(localName))
                {
                    mod = new WorkshopMod { WorkshopId = workshopId, Name = localName };
                }
                else
                {
                    StatusText = $"Fetching info for workshop item {workshopId}...";
                    mod = await _steam.GetWorkshopModInfo(workshopId).ConfigureAwait(true);
                }

                mod.Downloaded = true;
                mod.Status = "Ready";
                mod.PrimaryFilename = await Task.Run(() => _goldberg.DetectPrimaryFilename(targetDir)).ConfigureAwait(true);
                mod.SizeDisplay = await Task.Run(() => FormatFolderSize(targetDir)).ConfigureAwait(true);
                AddModToList(mod);
                added++;
            }

            MainWindowEnabled = true;
            StatusText = skipped > 0
                ? $"Added {added} mod(s), skipped {skipped} already in list. Ready."
                : $"Added {added} mod(s). Ready.";
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var dst = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
                File.SetAttributes(dst, FileAttributes.Normal);
            }
        }

        /// <summary>
        /// Looks for a Paradox-style descriptor (.mod file) in <paramref name="folderPath"/> and
        /// returns the value of the first <c>name = "..."</c> line, or null if none is found.
        /// </summary>
        private static string TryReadModNameFromFolder(string folderPath)
        {
            // Prefer descriptor.mod; fall back to any *.mod in the root.
            var candidates = new[] { Path.Combine(folderPath, "descriptor.mod") }
                .Concat(Directory.GetFiles(folderPath, "*.mod", SearchOption.TopDirectoryOnly))
                .Distinct();

            foreach (var file in candidates)
            {
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadAllLines(file))
                {
                    var m = Regex.Match(line, @"^\s*name\s*=\s*""([^""]+)""");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            return null;
        }

        private static string FormatFolderSize(string path)
        {
            if (!Directory.Exists(path)) return string.Empty;
            var bytes = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            return FormatBytes(bytes);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
            return $"{bytes} B";
        }


        private async Task RemoveWorkshopMod(WorkshopMod mod)
        {
            if (mod == null) return;

            WorkshopMods.Remove(mod);

            if (!GetDllPathDir(out var dirPath))
            {
                StatusText = $"Removed mod {mod.WorkshopId} from list. Ready.";
                return;
            }

            var activeDir   = Path.Combine(dirPath, "steam_settings", "mods",          mod.WorkshopId.ToString());
            var disabledDir = Path.Combine(dirPath, "steam_settings", "mods_disabled", mod.WorkshopId.ToString());

            await Task.Run(() =>
            {
                if (Directory.Exists(activeDir))   DeleteDirectory(activeDir);
                if (Directory.Exists(disabledDir)) DeleteDirectory(disabledDir);
            }).ConfigureAwait(true);

            StatusText = $"Removed mod {mod.WorkshopId} and deleted its files. Ready.";
        }

        private static void DeleteDirectory(string path)
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }

        private void MoveModUp(WorkshopMod mod)
        {
            if (mod == null || _workshopMods == null) return;
            var idx = _workshopMods.IndexOf(mod);
            if (idx > 0) _workshopMods.Move(idx, idx - 1);
        }

        private void MoveModDown(WorkshopMod mod)
        {
            if (mod == null || _workshopMods == null) return;
            var idx = _workshopMods.IndexOf(mod);
            if (idx >= 0 && idx < _workshopMods.Count - 1) _workshopMods.Move(idx, idx + 1);
        }

        // -----------------------------------------------------------------------
        // Controller command implementations
        // -----------------------------------------------------------------------

        /// <summary>
        /// Auto-fetches a controller VDF (or template info) from Steam using the game's AppID.
        /// On success, populates ControllerActionSets.  For template-only games, shows the
        /// template name in the info banner instead of populating the table.
        /// </summary>
        private async Task FetchControllerConfig()
        {
            if (AppId <= 0) { StatusText = "Set a valid AppID first! Ready."; return; }

            MainWindowEnabled = false;
            StatusText = $"Searching Steam for a controller config (App {AppId})...";
            try
            {
                var result = await _steam.GetControllerConfig(AppId).ConfigureAwait(true);

                // Always clear stale template info first
                _controllerTemplateName = null;
                RaisePropertyChanged(() => ControllerTemplateName);
                RaisePropertyChanged(() => ControllerTemplateVisible);

                if (result.ActionSets.Count > 0)
                {
                    ApplyFetchedControllerSets(result.ActionSets);
                    StatusText = $"Loaded {result.ActionSets.Count} action set(s) from Steam. Ready.";
                }
                else if (result.TemplateName != null)
                {
                    // Template or native-support game — XInput works natively; show the info banner
                    _controllerTemplateName = result.TemplateName;
                    RaisePropertyChanged(() => ControllerTemplateName);
                    RaisePropertyChanged(() => ControllerTemplateVisible);
                    StatusText = result.TemplateIndex.HasValue
                        ? $"Game uses controller template '{result.TemplateName}' — XInput works natively. Ready."
                        : $"Game has native controller support — XInput works without custom action sets. Ready.";
                }
                else
                {
                    StatusText =
                        $"No controller config found automatically. " +
                        $"Enter a VDF file ID from steamdb.info/app/{AppId}/config/ and press Fetch. Ready.";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error fetching controller config: {ex.Message}  Ready.";
            }
            finally
            {
                MainWindowEnabled = true;
            }
        }

        /// <summary>
        /// Fetches and parses the controller VDF identified by <see cref="ControllerVdfFileId"/>.
        /// </summary>
        private async Task FetchControllerByFileId()
        {
            if (!long.TryParse(ControllerVdfFileId?.Trim(), out var fileId) || fileId <= 0)
            {
                StatusText = "Enter a valid numeric published-file ID. Ready.";
                return;
            }

            MainWindowEnabled = false;
            StatusText = $"Downloading VDF file {fileId}...";
            try
            {
                var sets = await _steam.GetControllerActionSetsByFileId(fileId).ConfigureAwait(true);

                if (sets.Count > 0)
                {
                    ApplyFetchedControllerSets(sets);
                    StatusText = $"Loaded {sets.Count} action set(s) from file {fileId}. Ready.";
                }
                else
                {
                    StatusText = "Could not parse controller config from that file ID. Ready.";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error downloading VDF: {ex.Message}  Ready.";
            }
            finally
            {
                MainWindowEnabled = true;
            }
        }

        /// <summary>
        /// Replaces the current controller action sets with the fetched ones,
        /// keeping the selection on the first set.
        /// </summary>
        private void ApplyFetchedControllerSets(List<ControllerActionSet> sets)
        {
            ControllerActionSets = new ObservableCollection<ControllerActionSet>(sets);
            SelectedControllerActionSet = ControllerActionSets.FirstOrDefault();
        }

        private void AddControllerActionSet()
        {
            var name = "NewActionSet";
            var counter = 1;
            while (_controllerActionSets.Any(s => s.Name == name))
                name = $"NewActionSet{counter++}";
            var set = new ControllerActionSet { Name = name };
            _controllerActionSets.Add(set);
            SelectedControllerActionSet = set;
        }

        private void RemoveControllerActionSet()
        {
            if (_selectedControllerActionSet == null) return;
            var idx = _controllerActionSets.IndexOf(_selectedControllerActionSet);
            _controllerActionSets.Remove(_selectedControllerActionSet);
            SelectedControllerActionSet = _controllerActionSets.Count > 0
                ? _controllerActionSets[Math.Max(0, idx - 1)]
                : null;
        }

        private void AddControllerBinding()
        {
            _selectedControllerActionSet?.Bindings.Add(
                new ControllerBinding { ActionName = "Action", Binding = "A" });
        }

        private void RemoveControllerBinding(ControllerBinding binding)
        {
            _selectedControllerActionSet?.Bindings.Remove(binding);
        }

    }
}