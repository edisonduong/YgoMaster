using IL2CPP;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using UnityEngine;

namespace YgoMasterClient
{
    /// <summary>
    /// Tweaks to the home UI
    /// </summary>
    unsafe static class HomeViewTweaks
    {
        delegate void Del_UpdateDispPart(IntPtr thisPtr, int part);
        static Hook<Del_UpdateDispPart> hookUpdateDispPart;

        delegate void Del_UpdateHome(IntPtr thisPtr);
        static Hook<Del_UpdateHome> hookUpdateHome;
        static bool dumpedHomeHierarchy;
        static IntPtr movedTopics;

        static HomeViewTweaks()
        {
            if (Program.IsLive)
            {
                return;
            }

            IL2Assembly assembly = Assembler.GetAssembly("Assembly-CSharp");

            IL2Class headerClass = assembly.GetClass("HeaderViewController", "YgomGame.Menu");
            hookUpdateDispPart = new Hook<Del_UpdateDispPart>(UpdateDispPart, headerClass.GetMethod("UpdateDispPart"));

            IL2Class homeViewClass = assembly.GetClass("HomeViewController", "YgomGame.Menu");
            hookUpdateHome = new Hook<Del_UpdateHome>(UpdateHome, homeViewClass.GetMethod("UpdateHome"));
        }

        static void UpdateDispPart(IntPtr thisPtr, int part)
        {
            hookUpdateDispPart.Original(thisPtr, part);
            if (AssetHelper.IsQuitting)
            {
                return;
            }

            if (ClientSettings.HomeDisableUnusedHeaders)
            {
                IntPtr headerObj = Component.GetGameObject(thisPtr);
                DisableObject(headerObj, "HeaderUI(Clone).Root.RootTop.SafeArea.RootMenu.ButtonDuelPass");
                DisableObject(headerObj, "HeaderUI(Clone).Root.RootTop.SafeArea.RootMenu.ButtonNotice");
                DisableObject(headerObj, "HeaderUI(Clone).Root.RootTop.SafeArea.RootMenu.ButtonPresent");
                DisableObject(headerObj, "HeaderUI(Clone).Root.RootTop.SafeArea.RootMenu.ButtonDuelLive");
                if (string.IsNullOrEmpty(ClientSettings.MultiplayerToken))
                {
                    DisableObject(headerObj, "HeaderUI(Clone).Root.RootTop.SafeArea.RootMenu.ButtonFriend");
                }
            }
        }

        static void UpdateHome(IntPtr thisPtr)
        {
            // Hacky way to get proficiency test icon to show on the home screen but not the profile UI
            // NOTE: This is done because otherwise there's a big blank space where the icon should be
            // Alternative fix:
            // - Get this object "HomeUI_Console(Clone).Root.SafeAreaPlayer.RootPlayer.Player.ButtonPlayer.BaseGroup.Base"
            // - Decrease its RectTransform.offsetMax
            // - Get this object "HomeUI_Console(Clone).Root.SafeAreaPlayer.RootPlayer.Player.ButtonPlayer.BaseGroup.Over"
            // - Decrease its RectTransform.offsetMax
            YgomSystem.Utility.ClientWork.UpdateJson("{\"Certification\":{\"is_certification_open\":true}}");
            hookUpdateHome.Original(thisPtr);
            YgomSystem.Utility.ClientWork.DeleteByJsonPath("$.Certification");

            if (!dumpedHomeHierarchy)
            {
                dumpedHomeHierarchy = true;
                IntPtr homeObject = Component.GetGameObject(thisPtr);
                string hierarchy = GameObject.DumpFromRoot(homeObject);
                string path = Path.Combine(Program.ClientDataDir, "HomeHierarchy.json");
                File.WriteAllText(path, hierarchy);
                Console.WriteLine("[HomeViewTweaks] Wrote hierarchy to: " + path);
            }

            if (AssetHelper.IsQuitting)
            {
                return;
            }

            if (ClientSettings.HomeDisableUnusedTopics || ClientSettings.HomeDisableUnusedBanners)
            {
                IntPtr obj = Component.GetGameObject(thisPtr);
                if (ClientSettings.HomeDisableUnusedTopics)
                {
                    //DisableObject(obj, "HomeUI_Console(Clone).Root.SafeAreaTopics.RootTopics.Topics");
                    DisableObject(obj, "HomeUI_Console(Clone).Root.SafeAreaTopics.RootTopics.MissionBanner");
                }
                if (ClientSettings.HomeDisableUnusedBanners)
                {
                    DisableObject(obj, "HomeUI_Console(Clone).Root.SafeAreaMenu.RootMenu.BannerGroup.DuelShortcut");
                }
            }

            AddHomeButtons(thisPtr);
            ConfigureTopics(thisPtr); // TODO: Add a live config to toggle topics

        }

        sealed class HomeButtonDefinition
        {
            public string Name;
            public string TemplateName;
            public string Text;
            public string[] TextPaths;
            public string PositionReferenceName;
            public int SiblingIndex;
            public int VerticalOffset;
            public Action Action;
        }

        static readonly string[] HomeButtonTextPaths = new string[]
        {
            "Out.TextShadow",
            "Out.Text",
            "Over.Mask.TextOver",
            "GroupExplain.TextExplain"
        };

        static readonly HomeButtonDefinition[] customHomeButtons = new HomeButtonDefinition[]
        {
            new HomeButtonDefinition()
            {
                Name = "ButtonDiscord",
                TemplateName = "ButtonDeck",
                Text = "DISCORD",
                TextPaths = HomeButtonTextPaths,
                PositionReferenceName = "ButtonShop",
                SiblingIndex = 4,
                VerticalOffset = -70,
                Action = LinkToDiscord
            },
            new HomeButtonDefinition()
            {
                Name = "ButtonTest",
                TemplateName = "ButtonShop",
                Text = "Test Button",
                TextPaths = HomeButtonTextPaths,
                PositionReferenceName = "ButtonShop",
                SiblingIndex = 5,
                VerticalOffset = -140,
                Action = ShowTestButton
            }
        };

        static void AddHomeButtons(IntPtr thisPtr)
        {
            IntPtr homeObject = Component.GetGameObject(thisPtr);
            const string menuPath = "HomeUI_Console(Clone).Root.SafeAreaMenu.RootMenu";

            IntPtr menuContainer = GameObjectCloneUtils.Find(homeObject, menuPath);
            if (menuContainer == IntPtr.Zero)
            {
                return;
            }

            foreach (HomeButtonDefinition definition in customHomeButtons)
            {
                AddHomeButton(homeObject, menuContainer, menuPath, definition);
            }
        }

        static void AddHomeButton(
            IntPtr homeObject,
            IntPtr menuContainer,
            string menuPath,
            HomeButtonDefinition definition)
        {
            string templatePath = menuPath + "." + definition.TemplateName;
            if (GameObjectCloneUtils.Find(homeObject, templatePath) == IntPtr.Zero ||
                GameObject.FindGameObjectByName(menuContainer, definition.Name, false, false) != IntPtr.Zero)
            {
                return;
            }

            IntPtr positionReference = IntPtr.Zero;
            if (!string.IsNullOrEmpty(definition.PositionReferenceName))
            {
                positionReference = GameObjectCloneUtils.Find(
                    homeObject,
                    menuPath + "." + definition.PositionReferenceName);
                if (positionReference == IntPtr.Zero)
                {
                    return;
                }
            }

            IntPtr button = GameObjectCloneUtils.Clone(
                homeObject,
                templatePath,
                menuPath,
                definition.Name);
            if (button == IntPtr.Zero)
            {
                return;
            }

            GameObjectCloneUtils.SetSiblingIndex(button, definition.SiblingIndex);

            if (positionReference != IntPtr.Zero)
            {
                Vector3 position = GameObjectCloneUtils.GetLocalPosition(positionReference);
                position.y += definition.VerticalOffset;
                GameObjectCloneUtils.SetLocalPosition(button, position);
            }
            if (definition.TextPaths != null)
            {
                foreach (string textPath in definition.TextPaths)
                {
                    GameObjectCloneUtils.SetText(button, textPath, definition.Text);
                }
            }

            GameObjectCloneUtils.ReplaceSelectionButtonAction(button, definition.Action);
        }

        static void LinkToDiscord()
        {
            YgomGame.Menu.CommonDialogViewController.OpenYesNoConfirmationDialogScroll(
                "Join Discord",
                "Would you like to join our Discord server?",
                OpenGoogle,
                null,
                "Join",
                "Cancel");
        }

        static void OpenGoogle()
        {
            Process.Start("explorer.exe", "https://www.google.com");
        }

        static void ShowTestButton()
        {
            YgomGame.Menu.CommonDialogViewController.OpenAlertDialog(
                "Test Button",
                "This is a test button.",
                null,
                "OK");
        }

        static void ConfigureTopics(IntPtr thisPtr)
        {
            IntPtr homeObject = Component.GetGameObject(thisPtr);

            IntPtr topics = GameObjectCloneUtils.Find(
                homeObject,
                "HomeUI_Console(Clone).Root.SafeAreaTopics.RootTopics.Topics");

            if (topics != IntPtr.Zero && movedTopics != topics)
            {
                Vector3 position = GameObjectCloneUtils.GetLocalPosition(topics);
                position.y -= 170;
                GameObjectCloneUtils.SetLocalPosition(topics, position);
                movedTopics = topics;
            }
        }

        static void DisableObject(IntPtr obj, string path)
        {
            IntPtr ptr = GameObject.FindGameObjectByPath(obj, path);
            if (ptr == IntPtr.Zero)
            {
                Console.WriteLine("[HomeViewTweaks] Failed to find '" + path + "'");
                return;
            }
            GameObject.SetActive(ptr, false);
        }
    }
}