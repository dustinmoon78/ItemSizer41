// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using EFT.InventoryLogic;
using HarmonyLib;

namespace ItemSizer41.Client
{
    /// <summary>
    /// 让图标哈希包含模板宽高。
    /// 依据：
    ///   - ItemViewFactory.cs:43 用 CalculateCellSize() 算请求尺寸（每格 63px+1）；
    ///   - ItemView.cs:646-647 把图标 Sprite 交给 MainImage 后调 SetNativeSize()，
    ///     所以 PNG 的像素尺寸 = 实际显示尺寸；
    ///   - IconsHash.HashForItem 只由 TemplateId / Togglable / Foldable / 弹匣可见弹数决定，不含宽高。
    /// 结论：尺寸变了但哈希不变，就永远复用旧尺寸的 PNG。这里补上宽高即可。
    ///
    /// 边界：只覆盖"模板级尺寸"（Template.Width/Height，尺寸模组改的就是这两个字段）。
    /// 附件 ExtraSize / 折叠态 / 容器内容造成的尺寸变化，本来就已经在原哈希里（Foldable 分支 + GetAllItemHashes 递归），
    /// 不需要在这里重复引入。
    /// </summary>
    [HarmonyPatch]
    internal static class IconSizeHashPatch
    {
        private static MethodBase _target;
        private static int _calls;
        private static string _sample;

        internal static MethodBase TargetMethod()
        {
            _target = AccessTools.Method(typeof(IconsHash), "GetItemHash");
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
                Plugin.Log.LogError("未解析到 IconsHash.GetItemHash，图标尺寸跟随不会生效");
                return;
            }

            Plugin.Log.LogInfo("后缀目标：" + _target.DeclaringType?.Name + "." + _target.Name);
        }

        private static void Postfix(Item item, ref int __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.SizeFollow.Value || item == null)
            {
                return;
            }

            ItemTemplate template = item.Template;
            if (template == null)
            {
                return;
            }

            __result ^= unchecked((template.Width * 73856093) ^ (template.Height * 19349663));

            _calls++;
            if (_calls == 1)
            {
                _sample = template.Width + "x" + template.Height;
                Plugin.Log.LogInfo("哈希后缀已被调用（首例模板 " + _sample + "）");
            }
            else if (_calls % 5000 == 0)
            {
                Plugin.Log.LogInfo("哈希后缀累计调用 " + _calls + " 次（最近模板 " + template.Width + "x" + template.Height + "）");
            }
        }
    }
}
