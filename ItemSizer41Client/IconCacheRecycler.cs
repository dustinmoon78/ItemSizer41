// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Threading.Tasks;

namespace ItemSizer41.Client
{
    /// <summary>
    /// 旧图标缓存回收。
    ///
    /// 为什么必须整层清：哈希里混入模板宽高后，旧 hash→fileId 条目与旧 PNG 两两互相引用，
    /// 光看 index.json 无法判断谁过期（IconCreatorBase 只增不改、EFT 自己零调用方会清理），
    /// 结果就是磁盘只增不减。
    ///
    /// 换代判定：世代串 = 哈希方案常量 + 尺寸开关状态，戳文件放 cfg 同目录。
    /// 注意：世代不覆盖"尺寸模组改了模板宽高"（服务端改数据，客户端只看到结果），
    /// 那种孤儿只能靠切换一次开关或下次换代时一并回收。
    ///
    /// 回收方式（关键）：不逐文件删，也不整体搬走版本目录 ——
    ///   版本目录 <Icon Cache>/<MatchingVersion>/ 下面除了物品图标，还有 Clothing/、PlayerIcons/ 两个子目录，
    ///   整体改名会把服装/头像缓存一起卷走并删掉。
    /// 所以顺序是：把版本目录改名到 *.recycle-<时间戳> → 立刻重建同名空目录 →
    ///   把回收目录里的**子目录**（服装/头像）原样搬回 → 清内存索引 → 写戳 → 后台删除只剩物品图标的回收目录。
    /// 关键帧只有 3~5 次 rename，O(1)，不会在"第一次打开背包"那一帧删上万个 PNG。
    /// </summary>
    internal static class IconCacheRecycler
    {
        private const string RecycledTag = ".recycle-";
        private const string EmptyDirReason = "空目录";
        private static bool _done;

        internal static void EnsureOnce(ItemIconCreator creator)
        {
            if (_done)
            {
                return;
            }

            _done = true;

            if (creator == null)
            {
                return;
            }

            // 直接用 IconCreatorBase 自己算好的目录，避免与它的 "?? Editor" 语义走偏
            string iconDir = creator._cachePath;
            if (string.IsNullOrEmpty(iconDir))
            {
                Plugin.Log.LogWarning("图标缓存目录未知（_cachePath 为空），本次跳过回收");
                return;
            }

            // 历史回收目录无论开关状态都清（廉价，且避免残留目录越堆越多）
            SweepRecycled(iconDir);

            if (!Plugin.CacheRecycle.Value)
            {
                return;
            }

            string stampPath = Plugin.StampPath;
            if (string.IsNullOrEmpty(stampPath))
            {
                Plugin.Log.LogWarning("世代戳路径不可用，本次跳过图标缓存回收");
                return;
            }

            try
            {
                if (!Directory.Exists(iconDir))
                {
                    // Init() 的 CheckCacheFolder() 一定会建目录；走到这里说明是异常态：
                    // 不写戳（下次还会再试），并且必须可见
                    Plugin.Log.LogWarning("图标缓存目录不存在，本次不回收：" + iconDir);
                    return;
                }

                string rejectReason;
                if (!LooksLikeIconCache(iconDir, out rejectReason))
                {
                    if (rejectReason == EmptyDirReason)
                    {
                        // 初装或 EFT 自己清过：正常态，不打扰玩家，也不写戳（下次有内容再判）
                        Plugin.Log.LogDebug("图标缓存目录还是空的，本次跳过回收");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("图标缓存目录不像图标缓存（" + rejectReason + "），本次跳过回收：" + iconDir);
                    }
                    return;
                }

                string generation = Plugin.Generation;
                string previous = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null;
                if (string.Equals(previous, generation, StringComparison.Ordinal))
                {
                    return;
                }

                string recycled = iconDir + RecycledTag + DateTime.Now.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                try
                {
                    Directory.Move(iconDir, recycled);
                }
                catch (Exception moveError)
                {
                    Plugin.Log.LogWarning("图标缓存目录改名失败，本次不回收：" + moveError.Message);
                    return;
                }

                Directory.CreateDirectory(iconDir);

                // 把服装/头像等子目录原样搬回，回收目录里只留物品图标的 index.json 与 png
                int restored = 0;
                int failed = 0;
                foreach (string sub in Directory.GetDirectories(recycled))
                {
                    string target = Path.Combine(iconDir, Path.GetFileName(sub));
                    try
                    {
                        if (!Directory.Exists(target))
                        {
                            Directory.Move(sub, target);
                        }
                        else
                        {
                            // 另一个 creator 的 CheckCacheFolder 可能抢先建了同名空目录：
                            // 直接 Move 会抛异常，整层留在回收目录里被删 —— 改成合并
                            MergeInto(sub, target);
                        }
                        restored++;
                    }
                    catch (Exception subError)
                    {
                        failed++;
                        Plugin.Log.LogWarning("子目录搬回失败（" + Path.GetFileName(sub) + "），该层图标会重新生成：" + subError.Message);
                    }
                }

                // 索引与文件编号都归零：新图标从 1 开始写进刚重建的空目录
                creator._fileCacheIndex?.Clear();
                creator._maxFileId = 0;
                // _memoryCacheIndex 不清：首次 GetItemIcon 时它必然为空（唯一填充点在本方法之后）

                try
                {
                    File.WriteAllText(stampPath, generation);
                }
                catch (Exception stampError)
                {
                    // 目录已换、索引已清：宁可下次再换代一次，也不能反过来留「戳写了但没换」的死锁
                    Plugin.Log.LogWarning("世代戳写入失败，下次启动会再换一次代（图标重画）：" + stampError.Message);
                }

                string subNote = failed > 0 ? ("，失败 " + failed + " 个") : string.Empty;
                Plugin.Log.LogInfo("图标缓存换代：" + Path.GetFileName(iconDir) + " → " + Path.GetFileName(recycled)
                    + "（世代 " + generation + "，保留子目录 " + restored + " 个" + subNote + "），旧物品图标后台删除");

                Task.Run(() => TryDelete(recycled));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("图标缓存回收失败（不影响游戏）：[" + e.GetType().Name + "] " + e.Message);
            }
        }

        /// <summary>
        /// 内容特征判定，不依赖目录名：SPT 把临时目录指到 sptappdata\live，父目录并不叫 "Icon Cache"。
        /// 只认「index.json + *.png + Clothing/PlayerIcons 子目录」这一组特征；
        /// 出现任何陌生条目直接放弃 —— 整层改名会连着未知文件一起删，宁可跳过也不能误删。
        /// </summary>
        private static bool LooksLikeIconCache(string dir, out string reason)
        {
            reason = null;
            string parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)
                || string.Equals(parent, Path.GetPathRoot(dir), StringComparison.OrdinalIgnoreCase))
            {
                reason = "目录层级过浅";
                return false;
            }

            bool hasIndex = false;
            bool hasKnownSub = false;
            int foreign = 0;
            string firstForeign = null;
            foreach (FileSystemInfo entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                string name = entry.Name;
                if (entry is DirectoryInfo)
                {
                    if (string.Equals(name, "Clothing", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "PlayerIcons", StringComparison.OrdinalIgnoreCase))
                    {
                        hasKnownSub = true;
                        continue;
                    }
                }
                else
                {
                    if (string.Equals(name, "index.json", StringComparison.OrdinalIgnoreCase))
                    {
                        hasIndex = true;
                        continue;
                    }
                    if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                foreign++;
                if (firstForeign == null)
                {
                    firstForeign = name;
                }
            }

            if (foreign > 0)
            {
                reason = "含 " + foreign + " 个非图标条目（首个：" + firstForeign + "）";
                return false;
            }
            if (!hasIndex && !hasKnownSub)
            {
                reason = EmptyDirReason;
                return false;
            }
            return true;
        }

        private static void SweepRecycled(string iconDir)
        {
            try
            {
                string parent = Path.GetDirectoryName(iconDir);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                {
                    return;
                }

                string prefix = Path.GetFileName(iconDir) + RecycledTag;
                foreach (string stale in Directory.GetDirectories(parent, prefix + "*"))
                {
                    // 只认本回收器产出的名字（<图标目录名>.recycle-yyyyMMddHHmmssfff-xxxxxx）：
                    // 父目录现在可能是 SPT 的 appdata 根，和 files/package 同层，不能扫到谁删谁
                    if (!IsRecycledSuffix(Path.GetFileName(stale).Substring(prefix.Length)))
                    {
                        continue;
                    }
                    string target = stale;
                    Task.Run(() => TryDelete(target));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("清理历史回收目录失败（不影响游戏）：" + e.Message);
            }
        }

        /// <summary>
        /// 把 from 整棵树合并进已存在的 to：目标已有的文件保留，缺的补上。
        /// 用在「子目录搬回时目标目录已被别人建出来」的分支，避免整层缓存被回收掉。
        /// </summary>
        private static void MergeInto(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                string dst = Path.Combine(to, Path.GetFileName(file));
                if (!File.Exists(dst))
                {
                    File.Move(file, dst);
                }
            }
            foreach (string dir in Directory.GetDirectories(from))
            {
                MergeInto(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }

        /// <summary>
        /// 回收目录后缀校验：17 位时间戳 + '-' + 6 位十六进制，共 24 字符。
        /// </summary>
        private static bool IsRecycledSuffix(string suffix)
        {
            if (suffix == null || suffix.Length != 24 || suffix[17] != '-')
            {
                return false;
            }

            long ticks;
            if (!long.TryParse(suffix.Substring(0, 17), out ticks))
            {
                return false;
            }

            for (int i = 18; i < 24; i++)
            {
                char c = suffix[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }
            return true;
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // 后台删除失败无所谓：下次启动 SweepRecycled 会再试一次
            }
        }
    }
}
