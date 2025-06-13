using ModTool.Interface;
using UnityEngine;

namespace Descendersmod
{
    public class Loader : ModBehaviour
    {
        private static GameObject root;

        public static void Load()
        {
            Debug.Log("[DeServerClient] Injected!");

            root = new GameObject("DescendersMod");
            root.AddComponent<CustomBehaviour>();

            Object.DontDestroyOnLoad(root);
        }

        public static void Unload()
        {
            Debug.Log("[DeServerClient] Unloading…");
            if (root != null) Object.Destroy(root);
        }
    }
}
