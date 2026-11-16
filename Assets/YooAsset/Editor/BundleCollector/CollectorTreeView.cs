#if UNITY_2022_2_OR_NEWER
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace YooAsset.Editor
{
    internal enum CollectorTreeKind { Package, Group, Collector, PreviewFolder, PreviewAsset, Placeholder }

    /// <summary>只读树投影；预览节点借用收集器，不生成序列化配置。</summary>
    internal sealed class CollectorTreeNode
    {
        internal CollectorTreeKind Kind;
        internal BundleCollectorPackage Package;
        internal BundleCollectorGroup Group;
        internal BundleCollector Collector;
        internal string AssetPath;

        internal bool IsPreview
        {
            get { return Kind >= CollectorTreeKind.PreviewFolder; }
        }
        internal object Value
        {
            get { return Kind == CollectorTreeKind.Package ? (object)Package : Kind == CollectorTreeKind.Group ? Group : (object)Collector; }
        }
        internal string Key
        {
            get
            {
                string key = "p:" + Uri.EscapeDataString(Package.PackageName);
                if (Kind == CollectorTreeKind.Package) return key;
                key += "/g:" + Uri.EscapeDataString(Group.GroupName);
                if (Kind == CollectorTreeKind.Group) return key;
                key += "/c:" + Uri.EscapeDataString(Collector.CollectPath);
                return IsPreview ? key + "/a:" + Uri.EscapeDataString(AssetPath ?? string.Empty) : key;
            }
        }
        internal string Label
        {
            get
            {
                if (Kind == CollectorTreeKind.Package) return Package.PackageName;
                if (Kind == CollectorTreeKind.Group) return Group.GroupName;
                return Path.GetFileName((IsPreview ? AssetPath : Collector.CollectPath).TrimEnd('/'));
            }
        }

        internal static CollectorTreeNode ForPackage(BundleCollectorPackage package)
        {
            return new CollectorTreeNode { Kind = CollectorTreeKind.Package, Package = package };
        }
        internal static CollectorTreeNode ForGroup(BundleCollectorPackage package, BundleCollectorGroup group)
        {
            return new CollectorTreeNode { Kind = CollectorTreeKind.Group, Package = package, Group = group };
        }
        internal static CollectorTreeNode ForCollector(BundleCollectorPackage package, BundleCollectorGroup group, BundleCollector collector)
        {
            return new CollectorTreeNode { Kind = CollectorTreeKind.Collector, Package = package, Group = group, Collector = collector, AssetPath = collector.CollectPath };
        }
    }

    /// <summary>原生虚拟化树、懒加载资源投影和唯一选择上下文。</summary>
    internal sealed class CollectorTreeView
    {
        private readonly TreeView _tree;
        private readonly Action<IReadOnlyList<CollectorTreeNode>> _selectionChanged;
        private readonly Action<ContextualMenuPopulateEvent, CollectorTreeNode> _populateMenu;
        private const string DragKey = "YooAsset.CollectorTree.ConfigurationDrag";
        private readonly Action<CollectorTreeNode, string[]> _drop;
        private readonly Action<IReadOnlyList<CollectorTreeNode>, CollectorTreeNode> _move;
        private Vector2 _dragStart;
        private bool _dragPending;
        private CollectorTreeNode _dragSource;
        private readonly Dictionary<string, int> _ids = new Dictionary<string, int>();
        private readonly Dictionary<int, CollectorTreeNode> _nodes = new Dictionary<int, CollectorTreeNode>();
        private readonly Dictionary<BundleCollector, string[]> _previews = new Dictionary<BundleCollector, string[]>();
        private readonly HashSet<string> _expanded = new HashSet<string>();
        private List<CollectorTreeNode> _selected = new List<CollectorTreeNode>();
        private bool _refreshing;
        private string _search = string.Empty;
        private int _nextId = 1;

        internal TreeView Element
        {
            get { return _tree; }
        }
        internal IReadOnlyList<CollectorTreeNode> Selection
        {
            get { return _selected; }
        }
        internal IReadOnlyList<string> ExpandedKeys
        {
            get { return _expanded.ToList(); }
        }
        internal IReadOnlyList<string> SelectedKeys
        {
            get { return _selected.Select(n => n.Key).ToList(); }
        }

        internal CollectorTreeView(Action<IReadOnlyList<CollectorTreeNode>> selectionChanged,
            Action<ContextualMenuPopulateEvent, CollectorTreeNode> populateMenu, Action<CollectorTreeNode, string[]> drop,
            Action<IReadOnlyList<CollectorTreeNode>, CollectorTreeNode> move)
        {
            _selectionChanged = selectionChanged;
            _populateMenu = populateMenu;
            _drop = drop;
            _move = move;
            _tree = new TreeView { name = "CollectorTree", selectionType = SelectionType.Multiple, fixedItemHeight = 24, virtualizationMethod = CollectionVirtualizationMethod.FixedHeight };
            _tree.style.flexGrow = 1;
            _tree.style.minHeight = 0;
            _tree.makeItem = MakeRow;
            _tree.bindItem = BindRow;
            _tree.selectionChanged += OnSelectionChanged;
            _tree.itemExpandedChanged += args =>
            {
                if (_refreshing || !_nodes.TryGetValue(args.id, out var node)) return;
                if (args.isExpanded) _expanded.Add(node.Key); else _expanded.Remove(node.Key);
                _tree.schedule.Execute(() => Refresh());
            };
            _tree.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                var node = FindRowNode(evt.target as VisualElement);
                if (node != null && !_selected.Any(n => n.Key == node.Key)) Select(new[] { node });
                _populateMenu(evt, node);
            }));
            _tree.RegisterCallback<PointerDownEvent>(evt =>
            {
                _dragSource = FindRowNode(evt.target as VisualElement);
                _dragPending = evt.button == 0 && _dragSource != null && !_dragSource.IsPreview;
                _dragStart = evt.position;
            }, TrickleDown.TrickleDown);
            _tree.RegisterCallback<PointerUpEvent>(evt => { _dragPending = false; _dragSource = null; }, TrickleDown.TrickleDown);
            _tree.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape) { _dragPending = false; _dragSource = null; } }, TrickleDown.TrickleDown);
            _tree.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!_dragPending || Vector2.Distance(_dragStart, evt.position) < 8 || !CollectorTreeCommands.CanEdit(_selected)
                    || !CollectorTreeCommands.IsCurrent(_dragSource) || !_selected.Any(n => n.Value == _dragSource.Value)) return;
                _dragPending = false;
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
                DragAndDrop.SetGenericData(DragKey, _selected.ToList());
                DragAndDrop.StartDrag("Move collector configuration");
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);
            _tree.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                var target = FindRowNode(_tree.panel.Pick(evt.mousePosition));
                var sources = DragAndDrop.GetGenericData(DragKey) as List<CollectorTreeNode>;
                bool configurationDrag = DragAndDrop.objectReferences.Length == 0 && sources != null;
                DragAndDrop.visualMode = configurationDrag ? (CollectorTreeCommands.CanMove(sources, target) ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Rejected)
                    : target != null && target.Kind == CollectorTreeKind.Group && DragAndDrop.objectReferences.Length > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                evt.StopPropagation();
            });
            _tree.RegisterCallback<DragPerformEvent>(evt =>
            {
                var target = FindRowNode(_tree.panel.Pick(evt.mousePosition));
                var sources = DragAndDrop.GetGenericData(DragKey) as List<CollectorTreeNode>;
                if (DragAndDrop.objectReferences.Length == 0 && sources != null)
                {
                    if (CollectorTreeCommands.CanMove(sources, target)) { DragAndDrop.AcceptDrag(); _move(sources, target); }
                    DragAndDrop.SetGenericData(DragKey, null);
                }
                else if (target != null && target.Kind == CollectorTreeKind.Group && DragAndDrop.objectReferences.Length > 0)
                {
                    var paths = DragAndDrop.objectReferences.Select(AssetDatabase.GetAssetPath).ToArray();
                    DragAndDrop.AcceptDrag();
                    _drop(target, paths);
                }
                evt.StopPropagation();
            });
        }

        internal void Search(string search)
        {
            _search = (search ?? string.Empty).Trim();
            Refresh();
        }

        internal void Refresh(IReadOnlyList<CollectorTreeNode> selection = null, bool invalidatePreview = false)
        {
            var keys = (selection ?? _selected).Select(n => n.Key).ToList();
            // Rename/path 变更后 Key 从当前实例计算；Undo 后由窗口的序列化 Key 恢复。
            if (invalidatePreview) _previews.Clear();
            Rebuild(keys);
        }

        internal void Restore(IEnumerable<string> selected, IEnumerable<string> expanded)
        {
            _expanded.Clear();
            _expanded.UnionWith(expanded);
            _previews.Clear();
            Rebuild(selected.ToList());
        }

        internal void Select(IReadOnlyList<CollectorTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                _expanded.Add(CollectorTreeNode.ForPackage(node.Package).Key);
                if (node.Group != null) _expanded.Add(CollectorTreeNode.ForGroup(node.Package, node.Group).Key);
            }
            Rebuild(nodes.Select(n => n.Key).ToList());
            if (nodes.Count > 0 && _ids.TryGetValue(nodes[0].Key, out int id)) _tree.ScrollToItemById(id);
        }

        internal void FocusCollector(string packageName, string groupName, string path)
        {
            var package = BundleCollectorSettingData.Setting.Packages.FirstOrDefault(p => p.PackageName == packageName);
            var group = package?.Groups.FirstOrDefault(g => g.GroupName == groupName);
            var collector = group?.Collectors.FirstOrDefault(c => string.Equals(c.CollectPath, path, StringComparison.OrdinalIgnoreCase));
            if (collector == null)
            {
                Debug.LogWarning($"Collector not found: '{packageName}/{groupName}/{path}'.");
                return;
            }
            _search = string.Empty;
            Select(new[] { CollectorTreeNode.ForCollector(package, group, collector) });
        }

        private void Rebuild(List<string> selectedKeys)
        {
            _refreshing = true;
            try
            {
                _nodes.Clear();
                var roots = new List<TreeViewItemData<CollectorTreeNode>>();
                foreach (var package in BundleCollectorSettingData.Setting.Packages)
                {
                    var groups = new List<TreeViewItemData<CollectorTreeNode>>();
                    foreach (var group in package.Groups)
                    {
                        var collectors = new List<TreeViewItemData<CollectorTreeNode>>();
                        foreach (var collector in group.Collectors)
                        {
                            var node = CollectorTreeNode.ForCollector(package, group, collector);
                            var children = BuildPreview(node);
                            if (Matches(node) || children.Count > 0 || _search.Length == 0)
                                collectors.Add(Item(node, children));
                        }
                        var groupNode = CollectorTreeNode.ForGroup(package, group);
                        if (Matches(groupNode) || collectors.Count > 0 || _search.Length == 0) groups.Add(Item(groupNode, collectors));
                    }
                    var packageNode = CollectorTreeNode.ForPackage(package);
                    if (Matches(packageNode) || groups.Count > 0 || _search.Length == 0) roots.Add(Item(packageNode, groups));
                }
                _tree.SetRootItems(roots);
                _tree.Rebuild();
                foreach (var key in _expanded)
                    if (_ids.TryGetValue(key, out int id) && _nodes.ContainsKey(id)) _tree.ExpandItem(id);
                if (_search.Length > 0) _tree.ExpandAll();
                var selectedIds = selectedKeys.Where(k => _ids.ContainsKey(k) && _nodes.ContainsKey(_ids[k])).Select(k => _ids[k]).ToList();
                _tree.SetSelectionById(selectedIds);
                _selected = selectedIds.Select(id => _nodes[id]).ToList();
            }
            finally { _refreshing = false; }
            _selectionChanged(_selected);
        }

        private List<TreeViewItemData<CollectorTreeNode>> BuildPreview(CollectorTreeNode node)
        {
            var children = new List<TreeViewItemData<CollectorTreeNode>>();
            if (!AssetDatabase.IsValidFolder(node.AssetPath)) return children;
            if (_search.Length == 0 && !_expanded.Contains(node.Key))
            {
                var placeholder = new CollectorTreeNode { Kind = CollectorTreeKind.Placeholder, Package = node.Package, Group = node.Group, Collector = node.Collector, AssetPath = node.AssetPath + "/..." };
                children.Add(Item(placeholder, null));
                return children;
            }
            if (!_previews.TryGetValue(node.Collector, out var paths))
            {
                try
                {
                    node.Collector.CheckConfigError();
                    paths = node.Collector.GetAllCollectAssets(CollectorTreeCommands.CreateCollectCommand(node.Package), node.Group).Select(a => a.AssetInfo.AssetPath).ToArray();
                    _previews.Add(node.Collector, paths);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Cannot preview collector '{node.Collector.CollectPath}': {exception.Message}");
                    _previews.Add(node.Collector, Array.Empty<string>());
                    return children;
                }
            }
            string prefix = node.AssetPath.TrimEnd('/') + "/";
            var entries = paths.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(p =>
            {
                int slash = p.IndexOf('/', prefix.Length);
                return slash < 0 ? p : p.Substring(0, slash);
            }).Distinct().OrderBy(p => !AssetDatabase.IsValidFolder(p)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (string entry in entries)
            {
                var child = new CollectorTreeNode { Kind = AssetDatabase.IsValidFolder(entry) ? CollectorTreeKind.PreviewFolder : CollectorTreeKind.PreviewAsset,
                    Package = node.Package, Group = node.Group, Collector = node.Collector, AssetPath = entry };
                var descendants = child.Kind == CollectorTreeKind.PreviewFolder ? BuildPreview(child) : new List<TreeViewItemData<CollectorTreeNode>>();
                if (_search.Length == 0 || Matches(child) || descendants.Count > 0) children.Add(Item(child, descendants));
            }
            return children;
        }

        private bool Matches(CollectorTreeNode node)
        {
            if (_search.Length == 0) return true;
            return node.Label.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0
                || (node.AssetPath ?? string.Empty).IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private TreeViewItemData<CollectorTreeNode> Item(CollectorTreeNode node, List<TreeViewItemData<CollectorTreeNode>> children)
        {
            if (!_ids.TryGetValue(node.Key, out int id)) { id = _nextId++; _ids.Add(node.Key, id); }
            _nodes[id] = node;
            return new TreeViewItemData<CollectorTreeNode>(id, node, children);
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement { name = "CollectorTreeRow" };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            var icon = new Image { name = "Icon" };
            icon.style.width = 16; icon.style.height = 16; icon.style.marginRight = 5;
            row.Add(icon);
            var badge = new Label { name = "KindBadge", pickingMode = PickingMode.Ignore };
            badge.style.height = 18; badge.style.flexShrink = 0;
            badge.style.marginRight = 6;
            badge.style.paddingLeft = 8; badge.style.paddingRight = 8;
            badge.style.borderTopLeftRadius = 9; badge.style.borderTopRightRadius = 9;
            badge.style.borderBottomLeftRadius = 9; badge.style.borderBottomRightRadius = 9;
            badge.style.fontSize = 10;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(badge);
            var label = new Label { name = "Name" };
            label.style.flexGrow = 1;
            label.style.minWidth = 0;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(label);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            var node = _tree.GetItemDataForIndex<CollectorTreeNode>(index);
            row.userData = node;
            var badge = row.Q<Label>("KindBadge");
            bool isPackage = node.Kind == CollectorTreeKind.Package;
            bool isGroup = node.Kind == CollectorTreeKind.Group;
            badge.style.display = isPackage || isGroup ? DisplayStyle.Flex : DisplayStyle.None;
            badge.text = isPackage ? "package" : isGroup ? "group" : string.Empty;
            badge.style.backgroundColor = isPackage ? new Color(0.18f, 0.62f, 0.32f) : Color.white;
            badge.style.color = isPackage ? Color.white : new Color(0.15f, 0.15f, 0.15f);
            row.Q<Label>("Name").text = node.Kind == CollectorTreeKind.Placeholder ? "Expand to preview collected assets" : node.Label;
            row.tooltip = node.IsPreview ? $"Read-only preview: {node.AssetPath}\nOwner collector: {node.Collector.CollectPath}" : node.AssetPath ?? node.Label;
            row.Q<Image>("Icon").image = node.Kind <= CollectorTreeKind.Group ? EditorGUIUtility.IconContent("Folder Icon").image
                : AssetDatabase.GetCachedIcon(node.AssetPath);
            row.style.opacity = node.IsPreview ? 0.75f : 1f;
        }

        private void OnSelectionChanged(IEnumerable<object> values)
        {
            if (_refreshing) return;
            _selected = values.OfType<CollectorTreeNode>().Where(n => n.Kind != CollectorTreeKind.Placeholder).ToList();
            _selectionChanged(_selected);
        }

        private static CollectorTreeNode FindRowNode(VisualElement element)
        {
            for (; element != null; element = element.parent)
                if (element.userData is CollectorTreeNode node) return node.Kind == CollectorTreeKind.Placeholder ? null : node;
            return null;
        }
    }
}
#endif
