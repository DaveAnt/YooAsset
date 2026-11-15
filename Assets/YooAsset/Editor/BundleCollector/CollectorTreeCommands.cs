#if UNITY_2022_2_OR_NEWER
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YooAsset.Editor
{
    /// <summary>配置树的事务命令；剪贴板只复制配置，绝不操作工程资源文件。</summary>
    internal sealed class CollectorTreeCommands
    {
        private readonly Action<string> _recordUndo;
        private readonly Action<IReadOnlyList<CollectorTreeNode>> _changed;
        private List<CollectorTreeNode> _sources;
        private List<object> _copies;
        private CollectorTreeKind _clipboardKind;
        private bool _cut;

        internal bool HasClipboard
        {
            get { return _copies != null && _copies.Count > 0; }
        }

        internal CollectorTreeCommands(Action<string> recordUndo, Action<IReadOnlyList<CollectorTreeNode>> changed)
        {
            _recordUndo = recordUndo;
            _changed = changed;
        }

        internal static bool IsCurrent(CollectorTreeNode node)
        {
            if (node == null || !BundleCollectorSettingData.Setting.Packages.Contains(node.Package))
                return false;
            if (node.Kind == CollectorTreeKind.Package)
                return true;
            if (!node.Package.Groups.Contains(node.Group))
                return false;
            return node.Kind == CollectorTreeKind.Group || node.Group.Collectors.Contains(node.Collector);
        }

        internal static bool CanEdit(IReadOnlyList<CollectorTreeNode> nodes)
        {
            return nodes.Count > 0 && nodes.All(n => IsCurrent(n) && !n.IsPreview && n.Kind == nodes[0].Kind);
        }

        internal string PasteLabel(CollectorTreeNode target)
        {
            if (!HasClipboard)
                return "Paste (clipboard empty)";
            if (_clipboardKind == CollectorTreeKind.Package)
                return "Paste Package(s) at root";
            if (_clipboardKind == CollectorTreeKind.Group)
                return "Paste Group(s) into selected Package";
            return "Paste Collector(s) into selected Group";
        }

        internal bool CanPaste(CollectorTreeNode target)
        {
            if (!HasClipboard || (_cut && !_sources.All(IsCurrent)))
                return false;
            if (_clipboardKind == CollectorTreeKind.Package)
                return target == null || (IsCurrent(target) && target.Kind == CollectorTreeKind.Package);
            return IsCurrent(target) && ((_clipboardKind == CollectorTreeKind.Group && target.Kind == CollectorTreeKind.Package)
                || (_clipboardKind == CollectorTreeKind.Collector && target.Kind == CollectorTreeKind.Group));
        }

        internal void Copy(IReadOnlyList<CollectorTreeNode> nodes, bool cut = false)
        {
            RequireEditable(nodes);
            _sources = nodes.ToList();
            _clipboardKind = nodes[0].Kind;
            _copies = nodes.Select(n => Clone(n.Value)).ToList();
            _cut = cut;
        }

        internal void CreatePackage()
        {
            string name = UniqueName("DefaultPackage", BundleCollectorSettingData.Setting.Packages.Select(p => p.PackageName));
            _recordUndo("Add Package");
            var package = BundleCollectorSettingData.CreatePackage(name);
            _changed(new[] { CollectorTreeNode.ForPackage(package) });
        }

        internal void CreateGroup(CollectorTreeNode target)
        {
            RequireTarget(target, CollectorTreeKind.Package);
            string name = UniqueName("Default Group", target.Package.Groups.Select(g => g.GroupName));
            _recordUndo("Add Group");
            var group = BundleCollectorSettingData.CreateGroup(target.Package, name);
            _changed(new[] { CollectorTreeNode.ForGroup(target.Package, group) });
        }

        internal void AddCollectors(CollectorTreeNode target, IEnumerable<string> paths)
        {
            RequireTarget(target, CollectorTreeKind.Group);
            var collectors = paths.Select(path => NewCollector(path)).ToList();
            if (collectors.Count == 0)
                throw new InvalidOperationException("Select at least one project asset or folder to create a collector.");
            var candidate = ClonePackage(target.Package);
            var group = candidate.Groups[target.Package.Groups.IndexOf(target.Group)];
            group.Collectors.AddRange(collectors.Select(CloneCollector));
            ValidatePackage(candidate);
            _recordUndo("Add Collectors");
            foreach (var collector in collectors)
                BundleCollectorSettingData.CreateCollector(target.Group, collector);
            _changed(collectors.Select(c => CollectorTreeNode.ForCollector(target.Package, target.Group, c)).ToList());
        }

        internal void Rename(CollectorTreeNode node, string name)
        {
            if (!IsCurrent(node) || node.IsPreview)
                throw new InvalidOperationException("The configuration selection is no longer current. Select it again.");
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0)
                throw new ArgumentException("Name must not be empty.", nameof(name));
            if (node.Kind == CollectorTreeKind.Collector)
            {
                ChangePath(node, name);
                return;
            }
            var names = node.Kind == CollectorTreeKind.Package
                ? BundleCollectorSettingData.Setting.Packages.Where(p => p != node.Package).Select(p => p.PackageName)
                : node.Package.Groups.Where(g => g != node.Group).Select(g => g.GroupName);
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Name '{name}' already exists at this level. Choose a unique name.");
            if ((node.Kind == CollectorTreeKind.Package ? node.Package.PackageName : node.Group.GroupName) == name)
                return;
            _recordUndo("Rename Configuration");
            if (node.Kind == CollectorTreeKind.Package)
            {
                node.Package.PackageName = name;
                BundleCollectorSettingData.ModifyPackage(node.Package);
            }
            else
            {
                node.Group.GroupName = name;
                BundleCollectorSettingData.ModifyGroup(node.Package, node.Group);
            }
            _changed(new[] { node });
        }

        internal void ChangePath(CollectorTreeNode node, string path)
        {
            RequireTarget(node, CollectorTreeKind.Collector);
            var replacement = NewCollector(path);
            if (replacement.CollectPath == node.Collector.CollectPath)
                return;
            var candidate = ClonePackage(node.Package);
            var collector = candidate.Groups[node.Package.Groups.IndexOf(node.Group)].Collectors[node.Group.Collectors.IndexOf(node.Collector)];
            collector.CollectPath = replacement.CollectPath;
            collector.CollectorGUID = replacement.CollectorGUID;
            ValidatePackage(candidate);
            _recordUndo("Change Collector Path (configuration only)");
            node.Collector.CollectPath = replacement.CollectPath;
            node.Collector.CollectorGUID = replacement.CollectorGUID;
            BundleCollectorSettingData.ModifyCollector(node.Group, node.Collector);
            _changed(new[] { node });
        }

        internal void Remove(IReadOnlyList<CollectorTreeNode> nodes)
        {
            RequireEditable(nodes);
            _recordUndo("Remove Configuration");
            foreach (var node in nodes)
                RemoveOne(node);
            _changed(Array.Empty<CollectorTreeNode>());
        }

        internal void Paste(CollectorTreeNode target)
        {
            if (!CanPaste(target))
                throw new InvalidOperationException("Clipboard kind does not match this destination, or the cut source is no longer current. Paste packages at root, groups into a package, collectors into a group.");

            // 先构造最终配置快照并完整校验；失败时尚未记录 Undo，也未修改原配置。
            var originals = BundleCollectorSettingData.Setting.Packages;
            var candidates = originals.Select(ClonePackage).ToList();
            var additions = _copies.Select(Clone).ToList();
            if (_cut)
            {
                // 批量移除使用引用映射，避免 RemoveAt 后索引漂移。
                candidates = originals.Select(p => ClonePackageExcluding(p, _sources)).ToList();
            }
            if (_clipboardKind == CollectorTreeKind.Package)
            {
                var names = originals.Where(p => !_cut || !_sources.Any(s => s.Package == p)).Select(p => p.PackageName).ToList();
                foreach (BundleCollectorPackage package in additions)
                {
                    package.PackageName = UniqueName(package.PackageName, names);
                    names.Add(package.PackageName);
                    ValidatePackage(package);
                }
            }
            else
            {
                var candidate = candidates[originals.IndexOf(target.Package)];
                if (_clipboardKind == CollectorTreeKind.Group)
                {
                    var names = candidate.Groups.Select(g => g.GroupName).ToList();
                    foreach (BundleCollectorGroup group in additions)
                    {
                        group.GroupName = UniqueName(group.GroupName, names);
                        names.Add(group.GroupName);
                        candidate.Groups.Add(group);
                    }
                }
                else
                {
                    // Group 自身不可能同时是 Collector 剪切源，按原 Group 名定位。
                    var group = candidate.Groups[target.Package.Groups.IndexOf(target.Group)];
                    group.Collectors.AddRange(additions.Cast<BundleCollector>());
                }
                ValidatePackage(candidate);
            }

            _recordUndo(_cut ? "Move Configuration" : "Paste Configuration");
            if (_cut)
                foreach (var source in _sources)
                    RemoveOne(source);
            var selected = new List<CollectorTreeNode>();
            foreach (var value in additions)
            {
                if (value is BundleCollectorPackage package)
                {
                    var created = BundleCollectorSettingData.CreatePackage(package.PackageName);
                    CopyPackageFields(package, created);
                    selected.Add(CollectorTreeNode.ForPackage(created));
                }
                else if (value is BundleCollectorGroup group)
                {
                    var created = BundleCollectorSettingData.CreateGroup(target.Package, group.GroupName);
                    CopyGroupFields(group, created);
                    selected.Add(CollectorTreeNode.ForGroup(target.Package, created));
                }
                else
                {
                    var collector = (BundleCollector)value;
                    BundleCollectorSettingData.CreateCollector(target.Group, collector);
                    selected.Add(CollectorTreeNode.ForCollector(target.Package, target.Group, collector));
                }
            }
            if (_cut)
            {
                _sources = null;
                _copies = null;
                _cut = false;
            }
            _changed(selected);
        }

        internal static bool CanMove(IReadOnlyList<CollectorTreeNode> nodes, CollectorTreeNode target)
        {
            if (!CanEdit(nodes) || !IsCurrent(target) || target.IsPreview || nodes.Any(n => n.Value == target.Value)) return false;
            if (target.Kind == nodes[0].Kind)
                return nodes.All(n => SameParent(n, target));
            return (nodes[0].Kind == CollectorTreeKind.Group && target.Kind == CollectorTreeKind.Package)
                || (nodes[0].Kind == CollectorTreeKind.Collector && target.Kind == CollectorTreeKind.Group);
        }

        internal void Move(IReadOnlyList<CollectorTreeNode> nodes, CollectorTreeNode target)
        {
            if (!CanMove(nodes, target))
                throw new InvalidOperationException("Drop configuration onto its parent kind to move, or onto a same-parent sibling to reorder. Asset previews and cross-kind siblings are not destinations.");
            if (target.Kind == nodes[0].Kind)
            {
                _recordUndo("Reorder Configuration");
                if (target.Kind == CollectorTreeKind.Package)
                    Reorder(BundleCollectorSettingData.Setting.Packages, nodes.Select(n => n.Package).ToList(), target.Package);
                else if (target.Kind == CollectorTreeKind.Group)
                    Reorder(target.Package.Groups, nodes.Select(n => n.Group).ToList(), target.Group);
                else
                    Reorder(target.Group.Collectors, nodes.Select(n => n.Collector).ToList(), target.Collector);
                BundleCollectorSettingData.ModifyPackage(target.Package);
                _changed(nodes);
                return;
            }
            // 拖移复用剪切事务的同一预验，不占用用户配置剪贴板。
            var sources = _sources; var copies = _copies; var kind = _clipboardKind; bool cut = _cut;
            try { Copy(nodes, true); Paste(target); }
            finally { _sources = sources; _copies = copies; _clipboardKind = kind; _cut = cut; }
        }

        private static bool SameParent(CollectorTreeNode left, CollectorTreeNode right)
        {
            return left.Kind == CollectorTreeKind.Package || (left.Kind == CollectorTreeKind.Group ? left.Package == right.Package : left.Group == right.Group);
        }

        private static void Reorder<T>(List<T> list, List<T> sources, T before)
        {
            var ordered = list.Where(sources.Contains).ToList();
            list.RemoveAll(sources.Contains);
            list.InsertRange(list.IndexOf(before), ordered);
        }

        internal static CollectCommand CreateCollectCommand(BundleCollectorPackage package)
        {
            var command = new CollectCommand(package.PackageName, BundleCollectorSettingData.GetAssetIgnoreRuleInstance(package.IgnoreRuleName));
            command.SetFlag(ECollectFlags.IgnoreGetDependencies, true);
            command.UniqueBundleName = BundleCollectorSettingData.Setting.UniqueBundleName;
            command.EnableAddressable = package.EnableAddressable;
            command.SupportExtensionless = package.SupportExtensionless;
            command.LocationToLower = package.LocationToLower;
            command.IncludeAssetGUID = package.IncludeAssetGUID;
            command.AutoCollectShaders = package.AutoCollectShaders;
            return command;
        }

        internal static void ValidatePackage(BundleCollectorPackage package)
        {
            // 同一路径即使当前过滤为空也拒绝重复源；目录重叠按实际规则结果检查，允许互斥过滤。
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var collector in package.Groups.SelectMany(g => g.Collectors))
            {
                if (!paths.Add(collector.CollectPath.TrimEnd('/')))
                    throw new InvalidOperationException($"Duplicate collector source '{collector.CollectPath}' in package '{package.PackageName}'. Copy it to another package, or move the existing collector.");
            }
            package.CheckConfigError();
            package.GetCollectAssets(CreateCollectCommand(package));
        }

        internal static string UniqueName(string baseName, IEnumerable<string> names)
        {
            var used = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (!used.Contains(baseName))
                return baseName;
            int suffix = 1;
            while (used.Contains($"{baseName} ({suffix})"))
                suffix++;
            return $"{baseName} ({suffix})";
        }

        private static BundleCollector NewCollector(string path)
        {
            path = (path ?? string.Empty).Replace('\\', '/').Trim().TrimEnd('/');
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (guid.Length == 0 || !(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)))
                throw new ArgumentException($"Collector path '{path}' must be an existing project asset or folder under Assets/ or Packages/.", nameof(path));
            return new BundleCollector { CollectPath = path, CollectorGUID = guid };
        }

        private static void RequireEditable(IReadOnlyList<CollectorTreeNode> nodes)
        {
            if (!CanEdit(nodes))
                throw new InvalidOperationException("Select current configuration nodes of the same kind. Asset previews are read-only.");
        }

        private static void RequireTarget(CollectorTreeNode node, CollectorTreeKind kind)
        {
            if (!IsCurrent(node) || node.Kind != kind)
                throw new InvalidOperationException($"Select a current {kind} configuration node.");
        }

        private static void RemoveOne(CollectorTreeNode node)
        {
            if (node.Kind == CollectorTreeKind.Package)
                BundleCollectorSettingData.RemovePackage(node.Package);
            else if (node.Kind == CollectorTreeKind.Group)
                BundleCollectorSettingData.RemoveGroup(node.Package, node.Group);
            else
                BundleCollectorSettingData.RemoveCollector(node.Group, node.Collector);
        }

        private static object Clone(object value)
        {
            if (value is BundleCollectorPackage package)
                return ClonePackage(package);
            if (value is BundleCollectorGroup group)
                return CloneGroup(group);
            return CloneCollector((BundleCollector)value);
        }

        private static BundleCollector CloneCollector(BundleCollector source)
        {
            return JsonUtility.FromJson<BundleCollector>(JsonUtility.ToJson(source));
        }

        private static BundleCollectorGroup CloneGroup(BundleCollectorGroup source)
        {
            return JsonUtility.FromJson<BundleCollectorGroup>(JsonUtility.ToJson(source));
        }

        private static BundleCollectorPackage ClonePackage(BundleCollectorPackage source)
        {
            return JsonUtility.FromJson<BundleCollectorPackage>(JsonUtility.ToJson(source));
        }

        private static BundleCollectorPackage ClonePackageExcluding(BundleCollectorPackage source, List<CollectorTreeNode> excluded)
        {
            var clone = ClonePackage(source);
            for (int gi = source.Groups.Count - 1; gi >= 0; gi--)
            {
                var group = source.Groups[gi];
                if (excluded.Any(n => n.Kind == CollectorTreeKind.Group && n.Group == group))
                    clone.Groups.RemoveAt(gi);
                else
                    for (int ci = group.Collectors.Count - 1; ci >= 0; ci--)
                        if (excluded.Any(n => n.Kind == CollectorTreeKind.Collector && n.Collector == group.Collectors[ci]))
                            clone.Groups[gi].Collectors.RemoveAt(ci);
            }
            return clone;
        }

        private static void CopyPackageFields(BundleCollectorPackage source, BundleCollectorPackage destination)
        {
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), destination);
            BundleCollectorSettingData.ModifyPackage(destination);
        }

        private static void CopyGroupFields(BundleCollectorGroup source, BundleCollectorGroup destination)
        {
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), destination);
        }
    }
}
#endif
