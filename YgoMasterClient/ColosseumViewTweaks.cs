using IL2CPP;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace YgoMasterClient
{
    /// <summary>
    /// ColosseumViewTweaks is intentionally left as a no-op to avoid UI side effects.
    /// </summary>
    unsafe static class ColosseumViewTweaks
    {
        delegate void Del_UpdateMenu(IntPtr thisPtr);
        static Hook<Del_UpdateMenu> hookUpdateMenu;
        static IntPtr lastMenuObject;
        static string currentEventMessage;
        static readonly Random random = new Random();
        static readonly string[] eventMessages =
        {
            "A mysterious duelist is looking for a match.",
            "A rare card was spotted near the arena.",
            "The arena is quiet... for now.",
            "A new challenge may appear soon.",
            "The spectators are waiting for an exciting duel."
        };

        static ColosseumViewTweaks()
        {
            hookUpdateMenu = HookUtils.TryHook<Del_UpdateMenu>(UpdateMenu, "ColosseumViewController", "YgomGame.Colosseum", "UpdateMenu");
        }

        static void UpdateMenu(IntPtr thisPtr)
        {
            if (AssetHelper.IsQuitting)
            {
                return;
            }

            AddMoreButtons(thisPtr);
            HideDuelButtons(thisPtr);
            HideEvents(thisPtr);
        }

        static void HideDuelButtons(IntPtr thisPtr)
        {
            IntPtr menuObject = Component.GetGameObject(thisPtr);
            const string parentPath = "ColosseumUI(Clone).Root.RootMenu.GroupLeft.MenuGroup";

            try
            {
                GameObjectCloneUtils.SetChildrenActive(menuObject, parentPath, false);
            }
            catch
            {
                // Silent catch to avoid debug output
            }
        }

        static void HideEvents(IntPtr thisPtr)
        {
            IntPtr menuObject = Component.GetGameObject(thisPtr);
            const string parentPath = "ColosseumUI(Clone).Root.RootMenu.RootEvents";

            IntPtr menuContainer = GameObjectCloneUtils.Find(menuObject, parentPath);
            if (menuContainer == IntPtr.Zero)
            {
                return;
            }
            try
            {
                IntPtr emptyEvents = GameObjectCloneUtils.Find(menuContainer, "EmptyEvents");
                IntPtr label = GameObjectCloneUtils.Find(menuContainer, "Label");
                IntPtr eventScrollView = GameObjectCloneUtils.Find(menuContainer, "Infinity Vertical Single Scroll View");

                GameObjectCloneUtils.SetActive(emptyEvents, true);
                GameObjectCloneUtils.SetActive(label, false);
                GameObjectCloneUtils.SetActive(eventScrollView, false);

                if (lastMenuObject != menuObject)
                {
                    lastMenuObject = menuObject;
                    currentEventMessage = eventMessages[random.Next(eventMessages.Length)];
                }

                GameObjectCloneUtils.SetText(menuContainer, "EmptyEvents.Text", currentEventMessage);
            }
            catch
            {
                // Silent catch to avoid debug output
            }
        }

        static void AddMoreButtons(IntPtr thisPtr)
        {
            IntPtr menuObject = Component.GetGameObject(thisPtr);
            const string parentPath = "ColosseumUI(Clone).Root.TitleArea.ButtonArea";

            try
            {
                GameObjectCloneUtils.SetChildrenActive(menuObject, parentPath, false);
            }
            catch
            {
                // Silent catch to avoid debug output
            }
        }

    }
}
