using System;
using System.Collections.Generic;
using UnityEngine;

namespace YgoMasterClient
{
    /// <summary>
    /// Creates GameObjects from native resources, without depending on a live screen.
    /// Call on Unity's main thread after ResourceManager has initialized.
    /// Loaded prefabs retain one resource reference for the lifetime of the session,
    /// so clones can continue using their shared textures and materials.
    /// </summary>
    static class NativeAssetUtils
    {
        static readonly Dictionary<string, IntPtr> prefabs = new Dictionary<string, IntPtr>(StringComparer.Ordinal);

        /// <param name="objectPath">A copied scene path including Prefab(Clone), a
        /// prefab-relative slash/dot path, or an empty string for the entire prefab.
        /// Include the final object's name; the Inspector's top breadcrumb only names its parent.</param>
        /// <param name="nativePath">ResourceManager path, e.g. Prefabs/UI/Home/Console/HomeUI_Console.</param>
        /// <param name="parent">Destination GameObject (not its Transform).</param>
        /// <param name="name">Optional name for the new instance.</param>
        public static IntPtr Create(string objectPath, string nativePath, IntPtr parent, string name = null)
        {
            if (parent == IntPtr.Zero)
                throw new ArgumentException("A destination parent GameObject is required", "parent");
            if (string.IsNullOrWhiteSpace(nativePath))
                throw new ArgumentException("A native resource path is required", "nativePath");

            nativePath = nativePath.Trim().Replace('\\', '/').Trim('/');
            if (nativePath.Length == 0)
                throw new ArgumentException("A native resource path is required", "nativePath");

            IntPtr prefab;
            if (!prefabs.TryGetValue(nativePath, out prefab))
            {
                prefab = AssetHelper.LoadImmediateAsset(nativePath);
                if (prefab == IntPtr.Zero)
                    throw new InvalidOperationException("Could not load native prefab: " + nativePath);
                prefabs.Add(nativePath, prefab);
            }

            string[] children = GetChildNames(objectPath, UnityObject.GetName(prefab));
            IntPtr source = prefab;
            foreach (string child in children)
            {
                source = GameObjectCloneUtils.FindChild(source, child);
                if (source == IntPtr.Zero)
                    throw new InvalidOperationException("Native prefab '" + nativePath +
                        "' has no child '" + child + "' in '" + objectPath +
                        "'. The object may only exist on the live screen.");
            }

            IntPtr clone = GameObjectCloneUtils.Clone(source, parent,
                string.IsNullOrEmpty(name) ? UnityObject.GetName(source) + "Clone" : name);
            if (clone == IntPtr.Zero)
                throw new InvalidOperationException("Could not instantiate '" + objectPath + "' from " + nativePath);
            return clone;
        }

        /// <summary>Useful in Update hooks: reuse a named direct child instead of duplicating it.</summary>
        public static IntPtr CreateIfMissing(string objectPath, string nativePath, IntPtr parent, string name)
        {
            if (parent == IntPtr.Zero)
                throw new ArgumentException("A destination parent GameObject is required", "parent");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A stable instance name is required", "name");
            IntPtr existing = GameObjectCloneUtils.FindChild(parent, name);
            return existing != IntPtr.Zero ? existing : Create(objectPath, nativePath, parent, name);
        }

        internal static string[] GetChildNames(string objectPath, string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
                throw new ArgumentException("The loaded prefab has no name", "prefabName");
            if (string.IsNullOrWhiteSpace(objectPath))
                return new string[0];

            string path = objectPath.Trim().Replace('\\', '/');
            // Slash paths preserve spaces and periods in actual GameObject names.
            string[] parts = path.Split(new char[] { path.Contains("/") ? '/' : '.' }, StringSplitOptions.RemoveEmptyEntries);
            int start = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == prefabName + "(Clone)" || (i == 0 && parts[i] == prefabName))
                {
                    start = i + 1;
                    break;
                }
            }
            if (start == 0 && (Array.Exists(parts, x => x.EndsWith("(Clone)", StringComparison.Ordinal)) ||
                (parts.Length > 1 && parts[0] == "UI" && parts[1] == "ContentCanvas")))
            {
                throw new ArgumentException("The scene path does not contain the loaded prefab root '" +
                    prefabName + "(Clone)'", "objectPath");
            }
            string[] result = new string[parts.Length - start];
            Array.Copy(parts, start, result, 0, result.Length);
            return result;
        }
    }
}
