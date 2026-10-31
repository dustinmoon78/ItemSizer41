// SPDX-License-Identifier: AGPL-3.0-or-later
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ItemSizer41.Client
{
    /// <summary>
    /// 物品尺寸压缩 ItemSizer41（客户端件）：
    ///   1) 尺寸跟随：EFT 的图标哈希只看 TemplateId/可折叠/可开关/弹匣弹数，不看模板宽高；
    ///      而图标 PNG 的像素尺寸直接等于显示尺寸（ItemView.IconChangedHandler 里 SetNativeSize）。
    ///      于是尺寸类模组（ItemSizer41）改完宽高后，旧图标仍是旧尺寸、看着比格子大。
    ///      这里把模板宽高混进哈希，尺寸一变就换哈希，图标按新格子重画。
    ///   2) 缓存回收：哈希变化会让旧 hash→fileId 与旧 PNG 变成互相引用的孤儿，EFT 自己不回收。
    ///      这里用"世代戳"在首装/升级/切换开关时把物品图标层整层清一次。
    ///   3) 不可读纹理兜底：护甲插板这类 prefab 的图标直接取 PreviewPivot.Icon.overrideIcon 的 Sprite，
    ///      保存时对该 bundle 纹理 EncodeToPNG；纹理没开 Read/Write 就抛 ArgumentException，
    ///      这里先把纹理拷成可读再交给原逻辑保存。
    /// </summary>
    [BepInPlugin(Guid, DisplayName, Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string Guid = "com.zcode.itemsizer41.client";
        internal const string DisplayName = "物品尺寸压缩 ItemSizer41 BE 版 1";
        internal const string Version = "1.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> SizeFollow;
        internal static ConfigEntry<bool> ReadableFix;
        internal static ConfigEntry<bool> CacheRecycle;

        private Harmony _harmony;

        /// <summary>缓存世代戳文件（与 cfg 同目录，避免依赖 Paths 成员的版本差异）。</summary>
        internal static string StampPath;

        /// <summary>哈希方案标识：只在"哈希公式本身改了"时手动加一，与版本号解耦。</summary>
        internal const string HashScheme = "wh-v1";

        /// <summary>缓存世代：哈希方案 ＋ 尺寸开关状态任一变化，就应该清一次旧缓存。</summary>
        internal static string Generation => HashScheme + "|size=" + (SizeFollow.Value ? 1 : 0);

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("1. 常规", "启用", true,
                "总开关。关闭后行为与不装本插件完全一致。");

            SizeFollow = Config.Bind("2. 图标尺寸", "图标尺寸跟随格子", true,
                "开启后，图标哈希包含模板宽高：尺寸模组改了物品格子后，图标会按新格子重画，不再沿用旧尺寸的图标。");

            ReadableFix = Config.Bind("3. 纹理兜底", "修复不可读纹理", true,
                "护甲插板等物品的图标来自 bundle 里的精灵贴图；若贴图没开 Read/Write，保存图标会抛 ArgumentException。开启后先把贴图拷成可读再保存。");

            CacheRecycle = Config.Bind("4. 缓存回收", "自动回收旧图标缓存", true,
                "首装、升级或切换上面的尺寸开关时，自动把物品图标的旧缓存整个换掉（只清物品图标层，不动服装/头像图标）。注意：改尺寸模组的规则不会自动触发，想立刻回收可在 F12 里把这个开关关再开一次。");

            string configDir = System.IO.Path.GetDirectoryName(Config.ConfigFilePath);
            if (string.IsNullOrEmpty(configDir))
            {
                configDir = ".";
            }

            StampPath = System.IO.Path.Combine(configDir, Guid + ".cachegen");

            _harmony = new Harmony(Guid);

            MountPatch(typeof(IconCacheRecyclePatch), "缓存回收", () => IconCacheRecyclePatch.Report(), () => IconCacheRecyclePatch.LogResolvedTarget());
            MountPatch(typeof(IconSizeHashPatch), "图标尺寸", () => IconSizeHashPatch.Report(), () => IconSizeHashPatch.LogResolvedTarget());
            MountPatch(typeof(ReadableTexturePatch), "纹理兜底", () => ReadableTexturePatch.Report(), () => ReadableTexturePatch.LogResolvedTarget());
        }

        private void MountPatch(System.Type patchType, string label, System.Action report, System.Action diagnose)
        {
            try
            {
                _harmony.PatchAll(patchType);
                report();
            }
            catch (System.Exception e)
            {
                Log.LogError(label + "补丁挂载失败：" + e);
                diagnose();
            }
        }

        private void OnDestroy()
        {
            if (ReadableTexturePatch.FixedCount > 0)
            {
                Log.LogInfo("本次会话累计修复不可读纹理 " + ReadableTexturePatch.FixedCount + " 次（最近：" + ReadableTexturePatch.LastFixedName + "）");
            }

            _harmony?.UnpatchSelf();
        }
    }
}