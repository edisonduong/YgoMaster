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

        public static bool SetActive(IntPtr gameObject, bool active)
        {
            if (gameObject == IntPtr.Zero)
            {
                return false;
            }

            GameObject.SetActive(gameObject, active);
            return true;
        }

        public static bool SetActive(IntPtr root, string path, bool active)
        {
            return SetActive(Find(root, path), active);
        }

        public static int SetChildrenActive(IntPtr parent, bool active)
        {
            if (parent == IntPtr.Zero)
            {
                return 0;
            }

            int count = 0;
            foreach (IntPtr child in GameObject.GetChildren(parent))
            {
                GameObject.SetActive(child, active);
                count++;
            }
            return count;
        }

        public static int SetChildrenActive(IntPtr root, string path, bool active)
        {
            return SetChildrenActive(Find(root, path), active);
        }

        public static IntPtr Clone(IntPtr root, string templatePath, string parentPath, string cloneName)
        {
            IntPtr template = Find(root, templatePath);
            IntPtr parent = Find(root, parentPath);
            return Clone(template, parent, cloneName);
        }

        public static IntPtr Clone(IntPtr root, string templatePath, string cloneName)
        {
            IntPtr template = Find(root, templatePath);
            if (template == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return Clone(template, GameObject.GetParentObject(template), cloneName);
        }

        public static IntPtr Clone(IntPtr root, string templatePath)
        {
            IntPtr template = Find(root, templatePath);
            if (template == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return Clone(template, GameObject.GetParentObject(template), UnityObject.GetName(template) + "Clone");
        }

        public static IntPtr CloneIfMissing(IntPtr root, string templatePath, string cloneName)
        {
            IntPtr template = Find(root, templatePath);
            if (template == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr parent = GameObject.GetParentObject(template);
            if (parent == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr existingClone = FindChild(parent, cloneName);
            return existingClone != IntPtr.Zero ? existingClone : Clone(template, parent, cloneName);
        }

        public static IntPtr CloneIfMissing(IntPtr root, string templatePath, string parentPath, string cloneName)
        {
            IntPtr parent = Find(root, parentPath);
            if (parent == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr existingClone = FindChild(parent, cloneName);
            return existingClone != IntPtr.Zero ? existingClone : Clone(root, templatePath, parentPath, cloneName);
        }

        public static IntPtr Clone(IntPtr template, IntPtr parent, string cloneName)
        {
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

        public static void SetText(IntPtr gameObject, string text)
        {
            if (gameObject == IntPtr.Zero)
            {
                return;
            }

            if (bindingTextType != IntPtr.Zero)
            {
                IntPtr bindingText = GameObject.GetComponent(gameObject, bindingTextType);
                if (bindingText != IntPtr.Zero)
                {
                    YgomSystem.UI.BindingTextMeshProUGUI.SetTextId(bindingText, text);
                }
            }

            if (extendedTextType != IntPtr.Zero)
            {
                IntPtr textComponent = GameObject.GetComponent(gameObject, extendedTextType);
                if (textComponent != IntPtr.Zero)
                {
                    TMPro.TMP_Text.SetText(textComponent, text);
                }
            }
        }

        public static void SetText(IntPtr root, string path, string text)
        {
            SetText(Find(root, path), text);
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
