using IL2CPP;
using System;
using System.Collections.Generic;
using UnityEngine;
using YgoMaster;
using YgomSystem.Utility;

namespace YgoMasterClient
{
    // Uses the game's request transport and room screen; no second HTTP/session stack.
    unsafe static class ColosseumViewTweaks
    {
        delegate void Del_UpdateMenu(IntPtr thisPtr);
        static Hook<Del_UpdateMenu> hookUpdateMenu;
        static IntPtr controller;
        static IntPtr boundFind;
        static IntPtr boundDeck;
        static bool searching;
        static bool valid;
        static bool pending;
        static bool cancelRequested;
        public static bool IsStartingDuel { get; private set; }
        static DateTime nextPoll;
        static DateTime requestStarted;
        static string status = "Checking standard deck...";
        const string ButtonArea = "ColosseumUI(Clone).Root.TitleArea.ButtonArea";
        static readonly Action findAction = FindMatch;
        static readonly Action deckAction = SelectDeck;
        static IL2Method setInteractable;
        static IntPtr selectionButtonType;

        static ColosseumViewTweaks()
        {
            if (Program.IsLive) return;
            hookUpdateMenu = HookUtils.TryHook<Del_UpdateMenu>(UpdateMenu, "ColosseumViewController", "YgomGame.Colosseum", "UpdateMenu");
            IL2Class button = Assembler.GetAssembly("Assembly-CSharp").GetClass("SelectionButton", "YgomSystem.UI");
            selectionButtonType = button.IL2Typeof();
            // SelectionButton inherits Selectable in supported clients.
            for (IL2Class type = button; type != null && setInteractable == null; type = type.BaseType)
            {
                IL2Property property = type.GetProperty("interactable");
                if (property != null) setInteractable = property.GetSetMethod();
            }
        }

        static void UpdateMenu(IntPtr thisPtr)
        {
            hookUpdateMenu.Original(thisPtr);
            if (AssetHelper.IsQuitting) return;
            if (controller != thisPtr)
            {
                boundFind = IntPtr.Zero;
                boundDeck = IntPtr.Zero;
            }
            controller = thisPtr;
            valid = false;
            nextPoll = DateTime.MinValue;
            Render();
        }

        static bool IsVisible()
        {
            IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
            return controller != IntPtr.Zero && manager != IntPtr.Zero &&
                YgomSystem.UI.ViewControllerManager.GetStackTopViewController(manager) == controller;
        }

        static void Send(string command)
        {
            pending = true;
            requestStarted = DateTime.UtcNow;
            ClientWork.DeleteByJsonPath("Matchmaking");
            YgomSystem.Network.Request.Entry(command, "{}");
        }

        public static void Update()
        {
            if (Program.IsLive || AssetHelper.IsQuitting || controller == IntPtr.Zero) return;
            if (IsStartingDuel) return;
            bool visible = IsVisible();
            if (!visible && searching) cancelRequested = true;
            if (pending)
            {
                if (DateTime.UtcNow - requestStarted < TimeSpan.FromSeconds(35)) return;
                pending = false;
                searching = false;
                valid = false;
                cancelRequested = true;
                status = "Connection timed out. Search cancelled.";
            }
            if (cancelRequested)
            {
                cancelRequested = false;
                searching = false;
                Send("Matchmaking.cancel");
            }
            else if (visible && DateTime.UtcNow >= nextPoll)
            {
                nextPoll = DateTime.UtcNow.AddSeconds(2);
                Send(searching ? "Matchmaking.poll" : "Matchmaking.check");
            }
            if (visible) Render();
        }

        static void FindMatch()
        {
            if (searching)
            {
                cancelRequested = true;
                status = "Cancelling search...";
            }
            else if (valid && !pending)
            {
                searching = true;
                status = "Searching for an opponent...";
                Send("Matchmaking.join");
            }
            Render();
        }

        static void SelectDeck()
        {
            if (searching || pending) return;
            IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
            YgomSystem.UI.ViewControllerManager.PushChildViewController(manager, "DeckEdit/DeckSelect",
                new Dictionary<string, object> { { "GameMode", (int)GameMode.Rank } });
        }

        public static void OnNetworkComplete(string command, int code)
        {
            if (command == "Duel.end") IsStartingDuel = false;
            if (command == "Duel.begin" && IsStartingDuel && code != 0)
            {
                IsStartingDuel = false;
                cancelRequested = true;
                status = "Unable to start the duel. Please search again.";
            }
            if (!command.StartsWith("Matchmaking.", StringComparison.Ordinal)) return;
            pending = false;
            nextPoll = DateTime.UtcNow.AddSeconds(2);
            Dictionary<string, object> data = ClientWork.GetDict("Matchmaking");
            if (code != 0 || data == null)
            {
                searching = false;
                valid = false;
                status = "Unable to contact matchmaking. Please try again.";
                return;
            }
            valid = Utils.GetValue<bool>(data, "valid");
            string state = Utils.GetValue<string>(data, "state");
            string error = Utils.GetValue<string>(data, "error");
            if (cancelRequested || (!IsVisible() && searching))
            {
                cancelRequested = true;
                return;
            }
            searching = state == "searching";
            status = !string.IsNullOrEmpty(error) ? error : searching ?
                "Searching - position " + Utils.GetValue<int>(data, "position") + ", waited " +
                Utils.GetValue<int>(data, "wait_seconds") + "s. Estimated wait: unknown. Click Cancel Search to leave." :
                "Select a standard deck, then Find Match.";
            if (state == "matched" && IsVisible())
            {
                searching = false;
                IsStartingDuel = true;
                // Defer navigation until the network callback has unwound.
                TradeUtils.AddAction(() =>
                {
                    if (!IsVisible()) { IsStartingDuel = false; cancelRequested = true; return; }
                    DuelDll.OnDuelRoomBattleReady();
                    IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
                    // Use the game's PvP introduction: VS, coin toss, winner's turn
                    // selection, then Duel.begin. The controller's PrefabPath constant
                    // names the unavailable development asset Matching/TestDuelEntry.
                    // The shipped asset (confirmed in LoadedAssets.txt) is DuelStart.
                    IL2Assembly assembly = Assembler.GetAssembly("Assembly-CSharp");
                    IL2Class define = assembly.GetClass("PvpMenuDefine", "YgomGame.Menu");
                    const string path = "Matching/DuelStart";
                    string duelParamKey = define.GetField("ARGNAME_DPARAM").GetValue().GetValueObj<string>();
                    string gameModeKey = define.GetField("ARGNAME_GAMEMODE").GetValue().GetValueObj<string>();
                    YgomSystem.UI.ViewControllerManager.PushChildViewController(manager, path,
                        new Dictionary<string, object>
                        {
                            { duelParamKey, new Dictionary<string, object> { { gameModeKey, (int)GameMode.Room } } }
                        });
                });
            }
            if (IsVisible()) Render();
        }

        static void SetEnabled(IntPtr obj, bool enabled)
        {
            if (obj == IntPtr.Zero || setInteractable == null) return;
            IntPtr button = GameObject.GetComponent(obj, selectionButtonType);
            if (button != IntPtr.Zero) setInteractable.Invoke(button, new[] { new IntPtr(&enabled) });
        }

        static void Render()
        {
            if (!IsVisible()) return;
            IntPtr root = Component.GetGameObject(controller);
            GameObjectCloneUtils.SetChildrenActive(root, "ColosseumUI(Clone).Root.RootMenu.GroupLeft.MenuGroup", false);
            IntPtr area = GameObjectCloneUtils.Find(root, ButtonArea);
            if (area == IntPtr.Zero) return;
            IntPtr find = GameObjectCloneUtils.FindChild(area, "ButtonWCS");
            IntPtr deck = GameObjectCloneUtils.CloneIfMissing(root, ButtonArea + ".ButtonWCS", "ButtonMatchmakingDeck");
            GameObjectCloneUtils.SetActive(find, true);
            GameObjectCloneUtils.SetActive(deck, true);
            GameObjectCloneUtils.SetActive(root, ButtonArea + ".SeasonPointButton", false);
            foreach (IntPtr button in new[] { find, deck })
            {
                if (button == IntPtr.Zero) continue;
                GameObjectCloneUtils.SetActive(button, "Viewport", false);
                GameObjectCloneUtils.SetActive(button, "NumBadge", false);
                GameObjectCloneUtils.SetActive(button, "IconArea.IconGroup", false);
            }
            if (find != IntPtr.Zero)
            {
                GameObjectCloneUtils.SetText(find, "IconArea.TextEntry", searching ? "Cancel Search" : "Find Match");
                if (boundFind != find)
                {
                    GameObjectCloneUtils.ReplaceSelectionButtonAction(find, findAction);
                    boundFind = find;
                }
                SetEnabled(find, searching || (valid && !pending));
            }
            if (deck != IntPtr.Zero)
            {
                GameObjectCloneUtils.SetText(deck, "IconArea.TextEntry", "Select Standard Deck");
                if (boundDeck != deck)
                {
                    GameObjectCloneUtils.ReplaceSelectionButtonAction(deck, deckAction);
                    boundDeck = deck;
                }
                SetEnabled(deck, !searching && !pending);
            }
            const string events = "ColosseumUI(Clone).Root.RootMenu.RootEvents";
            GameObjectCloneUtils.SetActive(root, events + ".EmptyEvents", true);
            GameObjectCloneUtils.SetActive(root, events + ".Label", false);
            GameObjectCloneUtils.SetActive(root, events + ".Infinity Vertical Single Scroll View", false);
            GameObjectCloneUtils.SetText(root, events + ".EmptyEvents.Text", status);
        }
    }
}
