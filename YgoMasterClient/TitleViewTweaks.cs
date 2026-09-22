using System;
using UnityEngine;
using IL2CPP;

namespace YgoMasterClient
{
    unsafe static class TitleViewTweaks
    {
        delegate void Del_Update(IntPtr thisPtr);
        static Hook<Del_Update> hookUpdate;

        static TitleViewTweaks()
        {
            hookUpdate = HookUtils.TryHook<Del_Update>(Update, "TitleViewController", "YgomGame.Menu", "Update");
        }

        static void Update(IntPtr pointer)
        {
            hookUpdate.Original(pointer);
            HideTransferButton(pointer);
#if DEBUG
            AddDebugMessage(pointer);
            #endif
        }

        static void HideTransferButton(IntPtr pointer)
        {
            IntPtr gameObject = Component.GetGameObject(pointer);
            const string footerPath = "TitleUI(Clone).Root.Panel.SafeArea.FooterArea.FooterButtonGroup";
            IntPtr footer = GameObjectCloneUtils.Find(gameObject, footerPath);
            if (footer != IntPtr.Zero)
            {
                GameObjectCloneUtils.SetActive(footer, false);
            }
        }

        static void AddDebugMessage(IntPtr pointer)
        {
            IntPtr gameObject = Component.GetGameObject(pointer);
            const string parentPath = "TitleUI(Clone).Root.Panel.SafeArea";
            const string templateChildName = "DuelistID";
            const string codeVerChildName = "CodeVer";
            const string debugMessageName = "titleStatusLine";

            IntPtr parent = GameObjectCloneUtils.Find(gameObject, parentPath);
            if (parent == IntPtr.Zero)
            {
                return;
            }

            IntPtr existing = GameObjectCloneUtils.FindChild(parent, debugMessageName);
            if (existing != IntPtr.Zero)
            {
                return;
            }

            IntPtr duelistId = GameObjectCloneUtils.FindChild(parent, templateChildName);
            IntPtr codeVer = GameObjectCloneUtils.FindChild(parent, codeVerChildName);
            if (duelistId == IntPtr.Zero || codeVer == IntPtr.Zero)
            {
                return;
            }

            IntPtr clone = GameObjectCloneUtils.CloneChild(gameObject, parentPath, templateChildName, debugMessageName);
            if (clone == IntPtr.Zero)
            {
                return;
            }

            GameObjectCloneUtils.SetText(clone, "Running in DEBUG mode.");

            IntPtr duelistIdTransform = GameObject.GetTransform(duelistId);
            int duelistIdIndex = Transform.GetSiblingIndex(duelistIdTransform);

            Vector3 codeVerPos = GameObjectCloneUtils.GetLocalPosition(codeVer);
            Vector3 duelistIdPos = GameObjectCloneUtils.GetLocalPosition(duelistId);
            float spacing = duelistIdPos.y - codeVerPos.y;

            Vector3 clonePos = duelistIdPos;
            clonePos.y = duelistIdPos.y + spacing;

            GameObjectCloneUtils.SetSiblingIndex(clone, duelistIdIndex + 1);
            GameObjectCloneUtils.SetLocalPosition(clone, clonePos);
        }
    }
}
