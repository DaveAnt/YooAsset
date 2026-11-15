**YooAsset**是一套用于Unity3D的资源管理系统，用于帮助研发团队快速部署和交付游戏。

它可以满足商业化游戏的各类需求，并且经历多款百万DAU游戏产品的验证。

更多信息请了解官方主页：https://github.com/tuyoogame/YooAsset

本分支新增可选 `EFileNameStyle.SourceHashName = 3`，仅用于官方 `ScriptableBuildPipeline.Run`：按 SBP 实际写入归属，将源文件及 meta、Unity 内置原容器及实际对象虚拟 metadata 的 v1 集合 SHA256 写为 `<hash>.bundle`；v1 字节协议保留 `GameMain.SourceBundleHash\0v1\0` / `GameMain.UnityBuiltinObjects.v1`。它只改变清单、报告和发布文件名，真实文件 MD5/CRC/Size、缓存身份及依赖 ID 不变，其他管线在准备阶段明确拒绝，旧 0/1/2 样式和默认值不变。原始输入必须完全一致（包括路径大小写、meta、内置容器），不归一化内容、不回退产物哈希；构建输出 `source-inputs.json` 可核对源路径与真实文件校验。纯回归运行 `dotnet test Tools/SourceBundleHash.Tests/SourceBundleHash.Tests.csproj`，包内原生任务测试位于 Test Sample 的 Editor 目录。

