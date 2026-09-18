using System;
using UnityEngine;
using IL2CPP;

namespace YgoMasterClient
{
    unsafe static class TitleViewTweaks
    {
        delegate void Del_Update(IntPtr thisPtr);
        static Hook<Del_Update> hookUpdate;

        const string titleParentPath = "TitleUI(Clone).Root.Panel.SafeArea";
        const string customDuelButtonName = "MyDuelButton";
        const string customDuelButtonText = "DUELxxx";
        const string sourceObjectPath = "UI/ContentCanvas/ContentManager/Home/HomeUI_Console(Clone)/Root/SafeAreaMenu/RootMenu/ButtonDuel/";
        const string sourcePrefabPath = "Prefabs/UI/Home/Console/HomeUI_Console";
        const float customDuelButtonYOffset = -60f;

        static readonly string[] duelButtonTextPaths = new string[]
        {
            "Out.TextShadow",
            "Out.Text",
            "Over.Mask.TextOver"
        };

        static bool nativeButtonFailed;

        static TitleViewTweaks()
        {
            hookUpdate = HookUtils.TryHook<Del_Update>(Update, "TitleViewController", "YgomGame.Menu", "Update");
        }

        static void Update(IntPtr thisPtr)
        {
            hookUpdate.Original(thisPtr);
            AddNativeDuelButton(thisPtr);
        }

        static void AddNativeDuelButton(IntPtr thisPtr)
        {
            if (nativeButtonFailed)
                return;

            IntPtr root = Component.GetGameObject(thisPtr);
            if (root == IntPtr.Zero)
                return;

            IntPtr parent = GameObjectCloneUtils.Find(root, titleParentPath);
            if (parent == IntPtr.Zero)
                return;

            IntPtr existing = GameObjectCloneUtils.FindChild(parent, customDuelButtonName);
            if (existing != IntPtr.Zero)
                return;

            try
            {
                IntPtr button = NativeAssetUtils.Create(sourceObjectPath, sourcePrefabPath, parent, customDuelButtonName);
                foreach (string textPath in duelButtonTextPaths)
                {
                    GameObjectCloneUtils.SetText(button, textPath, customDuelButtonText);
                }
                GameObjectCloneUtils.SetText(button, "GroupExplain.TextExplain", "Hello world");
                Vector3 position = GameObjectCloneUtils.GetLocalPosition(button);
                position.y += customDuelButtonYOffset;
                GameObjectCloneUtils.SetLocalPosition(button, position);
            }
            catch (Exception ex)
            {
                nativeButtonFailed = true;
                Console.WriteLine("[TitleViewTweaks] Native Duel button clone failed: " + ex.Message);
            }
        }
    }
}
