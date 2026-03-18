# gbe_forkGUI

Fork of Jeddunk's [GoldbergGUI](https://forgejo.jeddunk.xyz/jeddunk/GoldbergGUI) modified to use Detanup01's [gbe_fork](https://github.com/Detanup01/gbe_fork). <br/> **For the sake of transparency, the vast majority of this project was done using Claude AI. I'm not a programmer or computer scientist, just an average guy who likes GoldbergGUI and wanted to keep using it with a more modern Steam emulator. I do my best to thoroughly test each software release, but I'm sure the codebase and code structure is far from perfect.**

## Installation

* Install the latest .NET Core Runtime by clicking
  [here](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-10.0.5-windows-x64-installer)
* Download the latest release and extract it into any folder (e.g. %USERPROFILE%\Desktop\gbe_forkGUI).

## Usage

* Double-click `gbe_forkGUI-VX.exe` to open the application.  
  _(When starting it for the first time, it might take a few seconds since it needs to
  cache a list of games available on the Steam Store and download the latest gbe_fork Emulator release.)_
* Click on the "Select..." button on the top right and select the `steam_api.dll` or `steam_api64.dll` file in the game folder.
* Enter the name of the game and click on the "Find ID..." button.
  * If it did not find the right game, either try again with more precise keywords,
    copy the app ID from the Steam Store to field to the right of the "Find ID..." button, 
    or use [SteamDB](https://steamdb.info/apps/) to find your game's AppID.
* Click the lower right "Get DLCs for AppID" button to fetch all available DLCs for the game. Select what DLCs should be turned on or off using the left column of checkboxes
  * Identical process for emulating achievements in the Achievements tab
* Set advanced options like "Offline mode" in the "Advanced" tab.
* Set global settings like application theme, account name, Steam64ID, and language in the "Global Settings" tab.
* Click on "Save".
* Click on "Revert" to completely undo all gbe_forkGUI, restoring the game to its unmodified state.

## Acknowledgment

[GoldbergGUI](https://forgejo.jeddunk.xyz/jeddunk/GoldbergGUI) is owned by (Jeddunk)[https://forgejo.jeddunk.xyz/jeddunk] and licensed under the GNU Lesser General Public License v3.0.
[Goldberg Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator/-/tree/master) is owned by (Mr. Goldberg)[https://gitlab.com/Mr_Goldberg] and licensed under GNU Lesser General Public License v3.0.
[gbe_fork](https://github.com/Detanup01/gbe_fork) is owned by (Detanup01)[https://github.com/Detanup01] and licensed under GNU Lesser General Public License v3.0.
[UrbanCMC](https://github.com/UrbanCMC/) - Implementation of achievements in GoldbergGUI

## License

gbe_forkGUI is licensed under the GNU General Public License v3.0.