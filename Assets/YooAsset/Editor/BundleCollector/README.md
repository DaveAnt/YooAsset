# Bundle Collector 配置树

Unity **2022.2 及以上**使用原生 UI Toolkit TreeView；Unity 2019.4–2022.1 保留原三列兼容界面。菜单入口仍为 **YooAsset → Bundle Collector**，`OpenWindow(packageName, groupName, collectPath)` 仍能定位收集器。

## 树与设置

- Package → Group → Collector 是配置层级。Collector 可以引用项目文件夹或单个资源。
- 展开目录收集器时按需投影当前规则实际收集的子目录/文件。预览节点继承所属 Collector，不新增逐文件配置。
- 右侧只显示当前选中配置的设置。多选显示操作上下文，不误显示其中某个节点的设置。预览右侧明确标记只读，并可定位所属 Collector。
- 搜索名称或路径时保留匹配节点的祖先。拖动中间分隔线调整树和设置区域宽度。
- Global Settings、规则别名、Package Settings、Save / Import / Export / Fix 保持可用。Show Packages 值为旧界面保存；配置树始终显示 Package 根。

## 操作（仅配置，不是磁盘文件管理器）

| 位置 | 操作 |
| --- | --- |
| 空白树区域 | 新 Package、粘贴 Package 到根 |
| Package | 新 Group、重命名、剪切/复制/粘贴 Package、粘贴 Group、移除配置 |
| Group | 从 Project 选择或选择文件/目录新增 Collector、重命名、剪切/复制、粘贴 Collector、移除配置 |
| Collector | 更改收集路径、剪切/复制、移除配置、Ping 源资源 |
| 资源预览 | 仅 Ping / Copy Path；没有文件创建、改名、移动或删除命令 |

剪贴板是配置的深复制，不共享 Group/Collector 列表。Package 粘贴到根，Group 粘贴到 Package，Collector 粘贴到 Group；不匹配的菜单禁用。创建/复制时同层名称自动加 ` (1)` 等后缀。

同一 Package 的重复收集路径和实际规则收集结果重叠会在修改前报错；跨 Package 可复制同一源。剪切移动排除自身旧配置后校验。批量配置操作先完整校验，再一次 Undo 提交，失败不产生半批修改。

Project 资源拖到 Group 只新增 Collector 配置。配置节点拖到对应父层级可移动；拖到同父的同类兄弟节点可按原顺序插到其前面。资源预览和错误层级不是配置拖放目标。

## 快捷键

树内支持 Ctrl/Shift 多选、`Ctrl+C/X/V`、`Ctrl+S`、`Delete` 和 `F2`。macOS 可用 Command 代替 Ctrl。

- Package / Group 的 F2 聚焦名称编辑。
- Collector 的 F2 编辑**收集路径配置**，绝不重命名实际文件。
- Delete 只移除配置，绝不删除 `Assets` 文件。
- TextField、ObjectField 和搜索输入获得焦点时拥有自己的输入快捷键，树不会抢占。

配置修改可 Undo / Redo，并恢复配置树选择和展开状态。仅选择、搜索、展开等视图操作不会标脏配置。
