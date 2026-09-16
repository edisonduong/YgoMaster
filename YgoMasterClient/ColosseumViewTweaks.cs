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
            if (Program.IsLive)
            {
                return;
            }
            try
            {
                IL2Assembly assembly = Assembler.GetAssembly("Assembly-CSharp");
                if (assembly == null)
                {
                    return;
                }

                // Look up the primary Colosseum view controller and attach the hook
                IL2Class colosseumViewClass = assembly.GetClass("ColosseumViewController", "YgomGame.Colosseum");
                if (colosseumViewClass == null)
                {
                    return;
                }

                var method = colosseumViewClass.GetMethod("UpdateMenu");
                if (method == null)
                {
                    return;
                }

                hookUpdateMenu = new Hook<Del_UpdateMenu>(UpdateMenu, method);
            }
            catch
            {
                return;
            }
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

            IntPtr menuContainer = GameObjectCloneUtils.Find(menuObject, parentPath);
            if (menuContainer == IntPtr.Zero)
            {
                return;
            }
            // Hide all buttons in the ButtonArea by disabling all child game objects
            try
            {
                List<IntPtr> children = GameObject.GetChildren(menuContainer);
                foreach (IntPtr child in children)
                {
                    GameObject.SetActive(child, false);
                }
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

                if (emptyEvents != IntPtr.Zero)
                {
                    GameObject.SetActive(emptyEvents, true);
                }
                if (label != IntPtr.Zero)
                {
                    GameObject.SetActive(label, false);
                }
                if (eventScrollView != IntPtr.Zero)
                {
                    GameObject.SetActive(eventScrollView, false);
                }

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

            IntPtr menuContainer = GameObjectCloneUtils.Find(menuObject, parentPath);
            if (menuContainer == IntPtr.Zero)
            {
                return;
            }
            // Hide all buttons in the ButtonArea by disabling all child game objects
            try
            {
                List<IntPtr> children = GameObject.GetChildren(menuContainer);
                foreach (IntPtr child in children)
                {
                    GameObject.SetActive(child, false);
                }
            }
            catch
            {
                // Silent catch to avoid debug output
            }
        }

    }
}
