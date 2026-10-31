// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

using IOPath = System.IO.Path;

namespace ItemSizer41;

/// <summary>
/// 物品尺寸压缩（4.1.5 自研重写，2026-09-19 扩展背包/弹挂）：
///   规则一 食品饮料 → 1×1（原 jeroFoodDrink1x1 的行为，MIT）；
///   规则二 弹匣 2×2 / 1×3 → 1×2（对齐 bushtail-SlimmerMagazines 的行为）；
///   规则三 背包网格×0.5 + 重量×0.5（按比例缩放，保持大小差异）；
///   规则四 弹挂网格×0.5 + 重量×0.5；
///   规则五 防弹衣（Armor）网格×0.5 + 重量×0.5。
///
/// 数据入口用 4.1 的 TemplateTable（4.0 的 DatabaseServer 已整个废除）。
///
/// TypePriority 取 1000002：必须晚于全部内容包（WTT-CBP 等在 Preload/100001 量级注册物品），
/// 否则内容包新增的物品拿不到覆盖。与 Scavs4All41 同档，落在 MLOM 阶段 3。
/// </summary>
[Injectable(InjectionType.Singleton, int.MaxValue, TypePriority = 1000002)]
public class ItemSizerEntry : IOnLoad
{
    private const string ModFolderName = "ItemSizer41";
    private const string ConfigFileName = "config.jsonc";

    private readonly ISptLogger<ItemSizerEntry> _logger;
    private readonly TemplateTable _templates;

    public ItemSizerEntry(ISptLogger<ItemSizerEntry> logger, TemplateTable templates)
    {
        _logger = logger;
        _templates = templates;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            SafeError($"物品尺寸压缩：初始化失败 {ex}");
        }

        return Task.CompletedTask;
    }

    private void Run()
    {
        var config = LoadConfig();
        if (config is null)
        {
            SafeError("物品尺寸压缩：配置不可用（缺失或解析失败），本次不做任何改动");
            return;
        }

        config.Normalize();

        if (!config.Enabled)
        {
            SafeInfo("物品尺寸压缩：已在 config.jsonc 中禁用（Enabled=false），不做任何改动");
            return;
        }

        var items = _templates.Items;
        if (items is null || items.Count == 0)
        {
            SafeError("物品尺寸压缩：模板表为空，跳过");
            return;
        }

        // 规则各自隔离：一条出问题不影响其他
        if (config.FoodDrink.Enabled)
        {
            try { ShrinkFoodAndDrink(items, config.FoodDrink, config.ConsoleLog); }
            catch (Exception ex) { SafeError($"物品尺寸压缩：食品饮料规则失败，已跳过 {ex}"); }
        }

        if (config.Magazine.Enabled)
        {
            try { ResizeMagazines(items, config.Magazine, config.ConsoleLog); }
            catch (Exception ex) { SafeError($"物品尺寸压缩：弹匣规则失败，已跳过 {ex}"); }
        }

        if (config.Backpack.Enabled)
        {
            try { ScaleItems(items, config.Backpack, "背包", config.ConsoleLog); }
            catch (Exception ex) { SafeError($"物品尺寸压缩：背包规则失败，已跳过 {ex}"); }
        }

        if (config.Rig.Enabled)
        {
            try { ScaleItems(items, config.Rig, "弹挂", config.ConsoleLog); }
            catch (Exception ex) { SafeError($"物品尺寸压缩：弹挂规则失败，已跳过 {ex}"); }
        }

        if (config.Armor.Enabled)
        {
            try { ScaleItems(items, config.Armor, "防弹衣", config.ConsoleLog); }
            catch (Exception ex) { SafeError($"物品尺寸压缩：防弹衣规则失败，已跳过 {ex}"); }
        }
    }

    /// <summary>规则一：Food / Drink 下尺寸大于 1×1 的一律压成 1×1。</summary>
    private void ShrinkFoodAndDrink(
        Dictionary<MongoId, TemplateItem> items,
        FoodDrinkConfig rule,
        bool consoleLog)
    {
        var parents = rule.ParentIds.Select(x => new MongoId(x)).ToHashSet();
        var excluded = rule.ExcludeItemIds.Select(x => new MongoId(x)).ToHashSet();

        var count = 0;
        foreach (var (id, item) in items)
        {
            if (!parents.Contains(item.Parent)) continue;
            if (excluded.Contains(id)) continue;

            var props = item.Properties;
            if (props?.Width is null || props.Height is null) continue;
            if (props.Width <= 1 && props.Height <= 1) continue;

            props.Width = 1;
            props.Height = 1;
            if (rule.ResetExtraSize) ResetExtraSize(props);
            count++;
        }

        SafeInfo($"物品尺寸压缩：食品饮料 → 1×1，{count} 件（父类 {parents.Count} 个，排除 {excluded.Count} 件）", consoleLog);
    }

    /// <summary>规则二：Magazine 下尺寸命中 From 列表的改为 To，可选清 ExtraSize*。</summary>
    private void ResizeMagazines(
        Dictionary<MongoId, TemplateItem> items,
        MagazineConfig rule,
        bool consoleLog)
    {
        var parent = new MongoId(rule.ParentId);
        var excluded = rule.ExcludeItemIds.Select(x => new MongoId(x)).ToHashSet();
        var from = rule.From
            .Where(x => x is { Count: >= 2 })
            .Select(x => (Width: x[0], Height: x[1]))
            .ToHashSet();
        var (toWidth, toHeight) = (rule.To[0], rule.To[1]);

        var count = 0;
        var skippedByExclude = 0;

        foreach (var (id, item) in items)
        {
            if (item.Parent != parent) continue;

            var props = item.Properties;
            if (props?.Width is null || props.Height is null) continue;
            if (!from.Contains((props.Width.Value, props.Height.Value))) continue;

            if (excluded.Contains(id)) { skippedByExclude++; continue; }

            props.Width = toWidth;
            props.Height = toHeight;
            if (rule.ResetExtraSize) ResetExtraSize(props);
            count++;
        }

        var excludeNote = skippedByExclude > 0 ? $"，另有 {skippedByExclude} 件在排除清单中跳过" : "";
        var fromNote = string.Join("/", from.Select(f => $"{f.Width}×{f.Height}"));
        SafeInfo($"物品尺寸压缩：弹匣 {fromNote} → {toWidth}×{toHeight}，{count} 件{excludeNote}", consoleLog);
    }

    /// <summary>
    /// 通用比例缩放：GridScale 改网格，WeightScale 改重量。
    /// 网格向下取整但不低于 MinGridSize（默认 2×2）。
    ///
    /// 汇总行永远输出「命中父类 N 件」，命中 0 会额外告警——
    /// 配置里父类 ID 写错时不能静默什么都不做。
    /// </summary>
    private void ScaleItems(
        Dictionary<MongoId, TemplateItem> items,
        ScaleConfig rule,
        string label,
        bool consoleLog)
    {
        var parent = new MongoId(rule.ParentId);
        var excluded = rule.ExcludeItemIds.Select(x => new MongoId(x)).ToHashSet();

        var matched = 0;              // 命中父类的件数
        var count = 0;                // 实际改动件数
        var skippedByExclude = 0;     // 排除清单跳过
        var skippedByAlreadySmall = 0;// 缩放后不比现在小 → 不动
        var skippedNoSize = 0;        // 无 Width/Height 字段

        foreach (var (id, item) in items)
        {
            if (item.Parent != parent) continue;
            matched++;

            if (excluded.Contains(id)) { skippedByExclude++; continue; }

            var props = item.Properties;
            if (props?.Width is null || props.Height is null) { skippedNoSize++; continue; }

            var newW = Math.Max(rule.MinGridSize, (int)Math.Floor(props.Width.Value * rule.GridScale));
            var newH = Math.Max(rule.MinGridSize, (int)Math.Floor(props.Height.Value * rule.GridScale));

            // 已经不比目标大（或等于）就不动，避免无意义写入
            if (newW >= props.Width.Value && newH >= props.Height.Value)
            {
                skippedByAlreadySmall++;
                continue;
            }

            props.Width = newW;
            props.Height = newH;

            if (props.Weight is not null && rule.WeightScale < 1.0)
            {
                props.Weight = Math.Round(props.Weight.Value * rule.WeightScale, 2);
            }

            if (rule.ResetExtraSize) ResetExtraSize(props);
            count++;
        }

        var pct = (int)(rule.GridScale * 100);
        var detail = new StringBuilder();
        detail.Append($"命中父类 {matched} 件");
        if (skippedByExclude > 0) detail.Append($"，排除 {skippedByExclude}");
        if (skippedByAlreadySmall > 0) detail.Append($"，尺寸已达标 {skippedByAlreadySmall}");
        if (skippedNoSize > 0) detail.Append($"，无尺寸字段 {skippedNoSize}");

        SafeInfo($"物品尺寸压缩：{label} 网格×{pct}% 重量×{(int)(rule.WeightScale * 100)}%，改动 {count} 件（{detail}）", consoleLog);

        if (matched == 0)
        {
            // 父类 ID 配错 / 该环境没有这类物品 —— 必须可见，不能静默
            SafeError($"物品尺寸压缩：{label} 规则未命中任何物品（父类 {rule.ParentId}），请检查 config.jsonc 的 ParentId");
        }
    }

    /// <summary>把四个 ExtraSize 字段归零。</summary>
    private static void ResetExtraSize(TemplateItemProperties props)
    {
        props.ExtraSizeUp = 0;
        props.ExtraSizeDown = 0;
        props.ExtraSizeLeft = 0;
        props.ExtraSizeRight = 0;
    }

    private Config? LoadConfig()
    {
        var path = ResolveConfigPath();
        if (path is null)
        {
            SafeError($"物品尺寸压缩：未找到 {ConfigFileName}");
            return null;
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true,
            };
            return JsonSerializer.Deserialize<Config>(json, options);
        }
        catch (Exception ex)
        {
            SafeError($"物品尺寸压缩：解析 {path} 失败：{ex.Message}");
            return null;
        }
    }

    private static string? ResolveConfigPath()
    {
        var assemblyFolder = IOPath.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(assemblyFolder))
        {
            var beside = IOPath.Combine(assemblyFolder, ConfigFileName);
            if (File.Exists(beside)) return beside;
        }

        var fromWorkingDirectory = IOPath.Combine(
            Directory.GetCurrentDirectory(), "user", "mods", ModFolderName, ConfigFileName);

        return File.Exists(fromWorkingDirectory) ? fromWorkingDirectory : null;
    }

    private void SafeInfo(string message, bool enabled = true)
    {
        if (!enabled) return;
        try { _logger.Info(message); } catch { }
    }

    private void SafeError(string message)
    {
        try { _logger.Error(message); } catch { }
    }
}
