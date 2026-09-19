Console commands available in the client

This document lists the console commands implemented in YgoMasterClient and a short description of what they do. Run these in the in-game client console (enable via ClientSettings.json -> ShowConsole = true).

- classes [filter]
  - Dumps Assembly-CSharp classes to ClientData/AssemblyClasses.txt. Optional filter restricts results.

- methods <ClassName|FullName> [Namespace]
  - Dumps methods/signatures for matching Assembly-CSharp class(es) to ClientData/ClassMethods-<ClassName>.txt.
  - Example: methods TitleViewController YgomGame.Title

- hierarchy
  - Dumps the current active view controller's GameObject hierarchy to ClientData/CurrentHierarchy.json.

- dumpassets
  - Dumps tracked raw native asset request paths to ClientData/LoadedAssets.txt.
  - This is based on asset paths seen by the client's ResourceManager load hooks.

- itemid [dumpInvalid?]
  - Creates JSON/text files for values in IDS_ITEM and related text groups.

- itemid_old
  - Older variant used for creating item id dumps.

- itemid_enum
  - Generates enum-style text for IDS_ITEM values.

- packnames
  - Gets all card pack names from live data.

- packimages
  - Attempts to discover card pack images (based on card IDs) and writes dump-packimages.txt.

- text <IDS_PATH>
  - Reads a single IDS_XXXX value (e.g. "IDS_CARD.STYLE3") and prints it.

- textenum <ENUM>
  - Dumps all text values for the given IDS enum.

- textdump
  - Dumps all IDS enums to the client dump folder.

- textreload
  - Reloads custom text data (IDS groups).

- soloreload
  - Reloads custom solo data sets.

- resultcodes
  - Extracts network result-code enums from YgomSystem.Network and writes dump-resultcodes.txt.

- locate <path>
- locateraw <path>
  - Finds where a /LocalData/ asset is located on disk (locateraw skips automatic path conversion).

- crc <path>
  - Prints an assetbundle-on-disk path and, if the file exists, opens Explorer selecting the file.

- carddata_path
  - Prints the internal card data IntIdPath in use by the game.

- carddata
  - Dumps several internal card data files (CARD_* and localization files) into the client dump folder.

- updatediff
  - Dumps referenced enums and functions to help diff and update local enum/function definitions when the client updates.

- updatejson <path> <value>
- updatejsonraw <json>
  - Update entries in the ClientWork json store (useful for testing server->client data flows).

- logjson <path>
  - Prints the JSON object at the given path from ClientWork.

- cardswithart
  - Lists cards that have art files but are missing from the game's CardRare data (requires CardData setup).

- vcargs
  - Prints the args object for the top view controller on the ContentViewControllerManager stack.

- pvpops
  - Generates a Pvp Ops enum (Pvp operation names) to a text file.

- unityplayerupdate
  - Recomputes UnityPlayer.dll function addresses based on a provided PDB (advanced; may crash if used incorrectly).

- bgreload
  - Reloads custom background assets.

- gacha_get_probability <gachaId> <shopId>
  - Requests gacha probability data via the API (used for debugging shop/gacha behavior).

- solo_clear
  - Clears all solo content for the account (used when resetting accounts for secret pack discovery).

- dismantle_all_cards <rarity...>
  - Dismantles every card you own of the listed rarity types (e.g. "dismantle_all_cards SuperRare UltraRare"). Use with caution.

- num_secrets
  - Logs the number of secret packs currently unlocked for the player.

- craft_secrets
  - Attempts to craft cards required to unlock all secret packs automatically.

- auto_free_pull
  - Automatically opens every pack with a free pull available in the current pack shop.

- card_base_data_size
  - Prints the expected size of internal CardBaseData (useful when updating native structs).

- pop
  - Miscellaneous debug helper (prints/pops internal state). Use only for debugging.

Notes
- Many commands write output files to the Program.ClientDataDir or Program.ClientDataDumpDir. See ClientSettings.json for enabling the client console.
- Some commands perform network/API requests and may require the game to be in specific screens or have live data loaded.
- Use these commands carefully; commands like dismantle_all_cards and solo_clear modify account data.

If you want these commands added to the root README.md instead, tell me and I will move this content there.
