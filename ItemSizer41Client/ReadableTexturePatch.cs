// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ItemSizer41.Client
{
    /// <summary>
    /// 前缀挂在 IconCreatorBase&lt;Item, ItemIcon&gt;.SaveIconAsync（ItemIconCreator 未重写它）。
    /// 若 icon.Sprite 的纹理不可读，先拷一份可读纹理换给图标，再让原方法照常保存。
    ///
    /// ★形参保持 ItemIcon（与目标形参类型一致，Harmony 才会原样绑定），
    ///   但方法内立刻转成 IResourceIcon 再用 —— Mono 对引用类型实参做泛型代码共享，
    ///   这个 detour 可能同时接住 ClothingIcon/PlayerIcon 等实现；只走接口成员访问才不会读错字段。
    ///
    /// ★为什么不回收“被顶替掉的旧 Sprite”：原版 IconCreatorBase.CleanIcon 清图标时会一并
    ///   Destroy(icon.Sprite.texture) 与 Destroy(icon.Sprite) —— 我们交出去的那份由它销毁，原代码没有泄漏；
    ///   而旧 Sprite 与图集共用（bundle 资产），销毁它会导致大面积图标变白且不可恢复。
    /// </summary>
    [HarmonyPatch]
    internal static class ReadableTexturePatch
    {
        private static MethodBase _target;
        private static int _fixedCount;
        private static string _lastName;

        internal static MethodBase TargetMethod()
        {
            _target = AccessTools.Method(typeof(ItemIconCreator), "SaveIconAsync");
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
                Plugin.Log.LogError("未解析到 ItemIconCreator.SaveIconAsync，不可读纹理兜底不会生效");
                return;
            }

            Plugin.Log.LogInfo("前缀目标：" + _target.DeclaringType?.Name + "." + _target.Name);
        }

        internal static int FixedCount => _fixedCount;

        internal static string LastFixedName => _lastName;

        private static void Prefix(ItemIcon icon)
        {
            if (!Plugin.Enabled.Value || !Plugin.ReadableFix.Value || icon == null)
            {
                return;
            }

            // 只通过接口访问：泛型共享下实参可能是别的 IResourceIcon 实现
            IResourceIcon resource = icon;
            if (resource == null)
            {
                return;
            }

            Texture2D source = null;
            try
            {
                Sprite sprite = resource.Sprite;
                if (sprite == null)
                {
                    return;
                }

                source = sprite.texture;
                if (source == null || source.isReadable)
                {
                    return;
                }

                Texture2D readable = null;
                try
                {
                    readable = CopyToReadable(source);
                    // 沿用原精灵的 pivot 与 PPU：SetNativeSize 按 rect/PPU 定尺寸，换掉会让"首帧 vs 复用帧"大小不一致
                    Rect originalRect = sprite.rect;
                    Vector2 pivot = (originalRect.width > 0f && originalRect.height > 0f)
                        ? new Vector2(sprite.pivot.x / originalRect.width, sprite.pivot.y / originalRect.height)
                        : Vector2.zero;
                    float pixelsPerUnit = (sprite.pixelsPerUnit > 0f) ? sprite.pixelsPerUnit : 100f;

                    // 旧 Sprite 不回收：它要么与图集共用（bundle 资产，销毁会毁图集），要么是上一轮本补丁交给
                    // CleanIcon 的那份（原版 CleanIcon 已 Destroy 其纹理与 Sprite 本体，见类头说明）。
                    resource.Sprite = Sprite.Create(readable, new Rect(0f, 0f, readable.width, readable.height), pivot, pixelsPerUnit);
                    readable = null; // 所有权已交给 Sprite，由 CleanIcon 负责销毁

                    _fixedCount++;
                    _lastName = source.name;
                    if (_fixedCount == 1)
                    {
                        Plugin.Log.LogInfo("首个不可读纹理已修：" + source.name + " " + source.width + "x" + source.height);
                    }
                    else if (_fixedCount % 200 == 0)
                    {
                        Plugin.Log.LogInfo("不可读纹理累计修复 " + _fixedCount + " 次（最近：" + _lastName + "）");
                    }
                }
                finally
                {
                    if (readable != null)
                    {
                        Object.Destroy(readable);
                    }
                }
            }
            catch (System.Exception e)
            {
                string who = (source == null) ? "未知纹理" : (source.name + " " + source.width + "x" + source.height);
                Plugin.Log.LogWarning("可读副本生成失败（" + who + "）：[" + e.GetType().Name + "] " + e.Message);
            }
        }

        private static Texture2D CopyToReadable(Texture2D source)
        {
            int width = source.width;
            int height = source.height;

            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            if (temporary == null)
            {
                throw new System.InvalidOperationException("RenderTexture.GetTemporary 返回 null");
            }

            RenderTexture previous = RenderTexture.active;
            Texture2D copy = null;
            bool handedOff = false;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;

                copy = new Texture2D(width, height, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                copy.Apply(false, false);
                copy.name = source.name + "_readable";
                copy.filterMode = FilterMode.Trilinear;
                handedOff = true;
                return copy;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (!handedOff && copy != null)
                {
                    Object.Destroy(copy);
                }
            }
        }
    }
}