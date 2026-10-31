// SPDX-License-Identifier: AGPL-3.0-or-later
namespace ItemSizer41;

/// <summary>
/// config.jsonc 映射。默认值写在字段初始化器里，语义约定：
///   · 整段缺失  → 保留初始化器 / 构造函数给的默认值
///   · 写 null    → 视为「未提供」，Normalize 落回默认
///   · 写 []      → 视为「显式清空」，正常为空表
/// </summary>
public class Config
{
    /// <summary>总开关：false = 什么也不改。</summary>
    public bool Enabled { get; set; } = true;

    public FoodDrinkConfig FoodDrink { get; set; } = new();

    public MagazineConfig Magazine { get; set; } = new();

    public BackpackConfig Backpack { get; set; } = new();

    public RigConfig Rig { get; set; } = new();

    public ArmorConfig Armor { get; set; } = new();

    /// <summary>每规则汇总行写日志开关。</summary>
    public bool ConsoleLog { get; set; } = true;

    public void Normalize()
    {
        FoodDrink = FoodDrink ?? new FoodDrinkConfig();
        Magazine = Magazine ?? new MagazineConfig();
        Backpack = Backpack ?? new BackpackConfig();
        Rig = Rig ?? new RigConfig();
        Armor = Armor ?? new ArmorConfig();
        FoodDrink.Normalize();
        Magazine.Normalize();
        Backpack.Normalize();
        Rig.Normalize();
        Armor.Normalize();
    }
}

/// <summary>规则一：食品饮料 → 1×1（原 4.0 mod jeroFoodDrink1x1 的行为，MIT）。</summary>
public class FoodDrinkConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Food / Drink 两个父类。</summary>
    public List<string> ParentIds { get; set; } = new()
    {
        "5448e8d04bdc2ddf718b4569", // Food
        "5448e8d64bdc2dce718b4568", // Drink
    };

    /// <summary>原 mod 的默认排除项：item_food_purewater（2×2 纯净水）。</summary>
    public List<string> ExcludeItemIds { get; set; } = new()
    {
        "5d1b33a686f7742523398398",
    };

    /// <summary>把 ExtraSize* 一并归零（否则压成 1×1 后网格里仍可能多占格）。</summary>
    public bool ResetExtraSize { get; set; } = true;

    public static List<string> DefaultParentIds() => new()
    {
        "5448e8d04bdc2ddf718b4569",
        "5448e8d64bdc2dce718b4568",
    };

    public static List<string> DefaultExcludeItemIds() => new()
    {
        "5d1b33a686f7742523398398",
    };

    public void Normalize()
    {
        ParentIds = ParentIds ?? DefaultParentIds();
        ExcludeItemIds = ExcludeItemIds ?? DefaultExcludeItemIds();
        ParentIds.RemoveAll(string.IsNullOrWhiteSpace);
        ExcludeItemIds.RemoveAll(string.IsNullOrWhiteSpace);
    }
}

/// <summary>规则二：弹匣瘦身（对齐 bushtail-SlimmerMagazines 行为）。</summary>
public class MagazineConfig
{
    public const string DefaultParentId = "5448bc234bdc2d3c308b4569";

    public bool Enabled { get; set; } = true;

    /// <summary>Magazine 父类。</summary>
    public string ParentId { get; set; } = DefaultParentId;

    /// <summary>命中这些原始尺寸才改。</summary>
    public List<List<int>> From { get; set; } = new()
    {
        new List<int> { 2, 2 },
        new List<int> { 1, 3 },
    };

    /// <summary>目标尺寸（[宽, 高]）。</summary>
    public List<int> To { get; set; } = new() { 1, 2 };

    public bool ResetExtraSize { get; set; } = true;

    public List<string> ExcludeItemIds { get; set; } = new();

    public static List<int> DefaultTo() => new() { 1, 2 };

    public void Normalize()
    {
        ParentId = string.IsNullOrWhiteSpace(ParentId) ? DefaultParentId : ParentId;
        From = From ?? new List<List<int>>();
        To = To ?? DefaultTo();
        ExcludeItemIds = ExcludeItemIds ?? new List<string>();
        ExcludeItemIds.RemoveAll(string.IsNullOrWhiteSpace);
        From.RemoveAll(x => x is null || x.Count < 2);
        if (To.Count < 2)
        {
            To = DefaultTo();
        }
    }
}

/// <summary>
/// 规则三/四的公共实现：按比例缩放网格与重量。
///
/// ★注意（踩过的坑）：ParentId 只有「这一个」属性，子类不得用 new 再声明同名字段。
/// 之前 BackpackConfig/RigConfig 用 new 遮蔽基类 ParentId，而 ScaleItems 的形参是
/// ScaleConfig，属性访问按静态类型绑定到基类那一份（空串），导致规则命中 0 件。
/// 默认父类改由虚方法 DefaultParentId() 提供，无遮蔽、无歧义。
/// </summary>
public class ScaleConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>物品父类 ID（唯一的一份，子类通过构造函数设初值）。</summary>
    public string ParentId { get; set; } = "";

    /// <summary>网格缩放比例（0~1）。0.5 = 宽高各取一半（向下取整，不低于 MinGridSize）。</summary>
    public double GridScale { get; set; } = 0.5;

    /// <summary>重量缩放比例（0~1）。1.0 = 不动重量。</summary>
    public double WeightScale { get; set; } = 0.5;

    /// <summary>缩放后网格的最小边长，防止出现 1×1 的背包/弹挂。</summary>
    public int MinGridSize { get; set; } = 2;

    /// <summary>把 ExtraSize* 一并归零。</summary>
    public bool ResetExtraSize { get; set; } = true;

    public List<string> ExcludeItemIds { get; set; } = new();

    /// <summary>本规则的默认父类 ID（子类覆盖）。</summary>
    protected virtual string DefaultParentId() => "";

    public void Normalize()
    {
        ParentId = string.IsNullOrWhiteSpace(ParentId) ? DefaultParentId() : ParentId.Trim();
        GridScale = Math.Clamp(GridScale, 0.0, 1.0);
        WeightScale = Math.Clamp(WeightScale, 0.0, 1.0);
        MinGridSize = Math.Clamp(MinGridSize, 1, 20);
        ExcludeItemIds = ExcludeItemIds ?? new List<string>();
        ExcludeItemIds.RemoveAll(string.IsNullOrWhiteSpace);
    }
}

/// <summary>规则三：背包压缩。父类 5448e53e4bdc2d60728b4567 = Backpack。</summary>
public class BackpackConfig : ScaleConfig
{
    public const string Parent = "5448e53e4bdc2d60728b4567";

    public BackpackConfig() => ParentId = Parent;

    protected override string DefaultParentId() => Parent;
}

/// <summary>规则四：弹挂压缩。父类 5448e5284bdc2dcb718b4567 = Vest（战术弹挂）。</summary>
public class RigConfig : ScaleConfig
{
    public const string Parent = "5448e5284bdc2dcb718b4567";

    public RigConfig() => ParentId = Parent;

    protected override string DefaultParentId() => Parent;
}

/// <summary>
/// 规则五：防弹衣压缩。父类 5448e54d4bdc2dcc718b4568 = Armor（护甲 / 防弹衣）。
///
/// 注意：护甲原始尺寸只有 3×3／3×4／4×3／4×4 四种，用 GridScale 0.5 + MinGridSize 2
/// 缩放后会全部触到下限 ⇒ 折叠后统一为 2×2（口径与背包/弹挂一致，不保留梯度）。
/// ParentId 继承自 ScaleConfig，本类不得再用 new 遮蔽（见 ScaleConfig 上方说明）。
/// </summary>
public class ArmorConfig : ScaleConfig
{
    public const string Parent = "5448e54d4bdc2dcc718b4568";

    public ArmorConfig() => ParentId = Parent;

    protected override string DefaultParentId() => Parent;
}
