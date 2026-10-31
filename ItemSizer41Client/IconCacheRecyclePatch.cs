// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using HarmonyLib;

namespace ItemSizer41.Client
{
    /// <summary>
    /// 前缀挂在 ItemIconCreator.GetItemIcon 上，只为在"第一次查图标"之前把过期缓存清掉。
    /// 挂这里而不是挂 IconsHash.GetItemHash，是因为要拿到实例上的 _fileCacheIndex / _maxFileId；
    /// 且 GetItemIcon 内部先算哈希再查缓存，前缀早于这次查询，不会用到半截清理过的目录。
    /// </summary>
    [HarmonyPatch]
    internal static class IconCacheRecyclePatch
    {
        private static MethodBase _target;

        internal static MethodBase TargetMethod()
        {
            _target = AccessTools.Method(typeof(ItemIconCreator), "GetItemIcon");
            return _target;
        }

        internal static void Report()
        {
            LogResolvedTarget();
        }

        internal static void LogResolvedTarget()
        {
            if (_target == null)
            {
                Plugin.Log.LogError("未解析到 ItemIconCreator.GetItemIcon，旧图标缓存不会自动回收");
                return;
            }

            Plugin.Log.LogInfo("前缀目标：" + _target.DeclaringType?.Name + "." + _target.Name);
        }

        private static void Prefix(ItemIconCreator __instance)
        {
            if (!Plugin.Enabled.Value)
            {
                return;
            }

            IconCacheRecycler.EnsureOnce(__instance);
        }
    }
}
