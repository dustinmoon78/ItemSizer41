// SPDX-License-Identifier: AGPL-3.0-or-later
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace ItemSizer41;

/// <summary>
/// 4.1.5 服务端 mod 元数据。注意 4.1 的 IModMetadata 没有 IsBundleMod，改为 HasPrepatcher。
/// </summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.zcode.itemsizer41";

    public string Name { get; init; } = "ItemSizer41";

    // 2026-10-09：本 mod 全部代码为独立编写，不含任何第三方源码，故不再挂任何上游署名。
    public string Author { get; init; } = "dustinmoon78、DeepSeek Harness";

    public List<string>? Contributors { get; init; }

    public bool HasPrepatcher { get; init; }

    public SemanticVersioning.Version Version { get; init; } = new(1, 1, 0);

    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");

    public List<string>? Incompatibilities { get; init; }

    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }

    public string? Url { get; init; }

    // 2026-10-09：协议由 MIT 改为 CC BY-NC-SA 4.0（对齐「原创模组统一协议」政策）。
    // 本 mod 为原创独立编写，不引用第三方源码，故不再标注任何上游许可或上游链接。
    public string License { get; init; } = "AGPL-3.0-or-later";
}
