using System;
using IL2CPP;
using UnityEngine;

namespace YgoMasterClient
{
    unsafe static class GameObjectCloneUtils
    {
        static readonly IntPtr selectionButtonType;
        static readonly IntPtr bindingTextType;
        static readonly IntPtr extendedTextType;
        static readonly IL2Field selectionButtonOnClick;
        static readonly IL2Method unityEventAddListener;
        static readonly IL2Method unityEventRemoveAllListeners;

        static GameObjectCloneUtils()
        {
            IL2Assembly assembly = Assembler.GetAssembly("Assembly-CSharp");
            IL2Class selectionButtonClass = assembly.GetClass("SelectionButton", "YgomSystem.UI");
            selectionButtonType = selectionButtonClass.IL2Typeof();
            bindingTextType = CastUtils.IL2Typeof("BindingTextMeshProUGUI", "YgomSystem.UI", "Assembly-CSharp");
            extendedTextType = CastUtils.IL2Typeof("ExtendedTextMeshProUGUI", "YgomSystem.YGomTMPro", "Assembly-CSharp");
            selectionButtonOnClick = selectionButtonClass.GetField("onClick");

            IL2Assembly coreModule = Assembler.GetAssembly("UnityEngine.CoreModule");
            unityEventAddListener = coreModule.GetClass("UnityEvent", "UnityEngine.Events").GetMethod("AddListener");
            unityEventRemoveAllListeners = coreModule.GetClass("UnityEventBase", "UnityEngine.Events").GetMethod("RemoveAllListeners");
        }

        public static IntPtr Find(IntPtr root, string path)
        {
            return GameObject.FindGameObjectByPath(root, path);
        }

        public static IntPtr FindChild(IntPtr parent, string name)
        {
            return GameObject.FindGameObjectByName(parent, name, false, false);
        }

        public static IntPtr Clone(IntPtr root, string templatePath, string parentPath, string cloneName)
        {
            IntPtr template = Find(root, templatePath);
            IntPtr parent = Find(root, parentPath);
            if (template == IntPtr.Zero || parent == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr clone = UnityObject.Instantiate(template, GameObject.GetTransform(parent));
            if (clone == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            UnityObject.SetName(clone, cloneName);
            GameObject.SetActive(clone, true);
            return clone;
        }

        public static void SetSiblingIndex(IntPtr gameObject, int index)
        {
            Transform.SetSiblingIndex(GameObject.GetTransform(gameObject), index);
        }

        public static void SetLocalPosition(IntPtr gameObject, Vector3 position)
        {
            Transform.SetLocalPosition(GameObject.GetTransform(gameObject), position);
        }

        public static Vector3 GetLocalPosition(IntPtr gameObject)
        {
            return Transform.GetLocalPosition(GameObject.GetTransform(gameObject));
        }

        public static void SetText(IntPtr root, string path, string text)
        {
            IntPtr textObject = Find(root, path);
            if (textObject == IntPtr.Zero)
            {
                return;
            }

            if (bindingTextType != IntPtr.Zero)
            {
                IntPtr bindingText = GameObject.GetComponent(textObject, bindingTextType);
                if (bindingText != IntPtr.Zero)
                {
                    YgomSystem.UI.BindingTextMeshProUGUI.SetTextId(bindingText, text);
                }
            }

            if (extendedTextType != IntPtr.Zero)
            {
                IntPtr textComponent = GameObject.GetComponent(textObject, extendedTextType);
                if (textComponent != IntPtr.Zero)
                {
                    TMPro.TMP_Text.SetText(textComponent, text);
                }
            }
        }

        public static void ReplaceSelectionButtonAction(IntPtr gameObject, Action action)
        {
            IntPtr selectionButton = GameObject.GetComponent(gameObject, selectionButtonType);
            if (selectionButton == IntPtr.Zero)
            {
                return;
            }

            IntPtr onClick = selectionButtonOnClick.GetValue(selectionButton).ptr;
            unityEventRemoveAllListeners.Invoke(onClick);
            IntPtr callback = UnityEngine.Events._UnityAction.CreateUnityAction(action);
            unityEventAddListener.Invoke(onClick, new IntPtr[] { callback });
        }
    }
}
