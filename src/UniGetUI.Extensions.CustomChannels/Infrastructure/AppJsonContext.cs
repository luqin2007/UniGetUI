using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Download;
using UniGetUI.Extensions.CustomChannels.Packages;
using UniGetUI.Extensions.CustomChannels.Pan;

namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

/// <summary>源生成 JSON 上下文：配置、断点计划、提权计划、转存清单。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdaterConfig))]
[JsonSerializable(typeof(TargetConfig))]
[JsonSerializable(typeof(SourceConfig))]
[JsonSerializable(typeof(DownloadPlanDocument))]
[JsonSerializable(typeof(ApplyPlanDocument))]
[JsonSerializable(typeof(ApplyStatusDocument))]
[JsonSerializable(typeof(TransferLedgerDocument))]
[JsonSerializable(typeof(UniGetUI.Extensions.CustomChannels.Update.BackupManifest))]
[JsonSerializable(typeof(UniGetUI.Extensions.CustomChannels.Update.VersionRecord))]
[JsonSerializable(typeof(UniGetUI.Extensions.CustomChannels.Packages.InstalledFileRecord))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class AppJsonContext : JsonSerializerContext;
