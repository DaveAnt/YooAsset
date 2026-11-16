#if UNITY_2022_2_OR_NEWER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Xml.Linq;
using UnityEditor.UIElements;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using YooAsset.Editor;

namespace YooAsset.Tests.Editor
{
    /// <summary>配置事务和只读投影回归。所有资源与 Setting 均为本测试独有。</summary>
    public sealed class CollectorTreeTests
    {
        private const string Root = "Assets/CollectorTreeNUnitArtifacts";
        private readonly FieldInfo _settingField = typeof(BundleCollectorSettingData).GetField("s_setting", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly FieldInfo _dirtyField = typeof(BundleCollectorSettingData).GetField("<IsDirty>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        private BundleCollectorSetting _previous;
        private bool _previousDirty;
        private BundleCollectorSetting _setting;
        private CollectorTreeCommands _commands;
        private IReadOnlyList<CollectorTreeNode> _selected;
        private int _undoRecords;
        private string _path;
        private string _secondPath;
        private bool _ownsArtifacts;
        private bool _swappedSetting;

        [SetUp]
        public void SetUp()
        {
            _previous = (BundleCollectorSetting)_settingField.GetValue(null);
            _previousDirty = (bool)_dirtyField.GetValue(null);
            Assert.IsFalse(AssetDatabase.IsValidFolder(Root), "Refuse to overwrite another test's artifacts.");
            AssetDatabase.CreateFolder("Assets", "CollectorTreeNUnitArtifacts");
            _ownsArtifacts = true;
            AssetDatabase.CreateFolder(Root, "Content");
            _path = Root + "/Content/one.txt";
            _secondPath = Root + "/Content/two.txt";
            File.WriteAllText(_path, "one");
            File.WriteAllText(_secondPath, "two");
            AssetDatabase.ImportAsset(_path, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(_secondPath, ImportAssetOptions.ForceSynchronousImport);
            _setting = ScriptableObject.CreateInstance<BundleCollectorSetting>();
            AssetDatabase.CreateAsset(_setting, Root + "/Setting.asset");
            _settingField.SetValue(null, _setting);
            _dirtyField.SetValue(null, false);
            _swappedSetting = true;
            _undoRecords = 0;
            _commands = new CollectorTreeCommands(operation => { _undoRecords++; Undo.FlushUndoRecordObjects(); Undo.IncrementCurrentGroup(); Undo.RecordObject(_setting, operation); }, selected => _selected = selected);
        }

        [TearDown]
        public void TearDown()
        {
            if (_setting != null) Undo.ClearUndo(_setting);
            if (_swappedSetting)
            {
                _settingField.SetValue(null, _previous);
                _dirtyField.SetValue(null, _previousDirty);
                _swappedSetting = false;
            }
            if (_ownsArtifacts && AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
            _ownsArtifacts = false;
        }

        [Test]
        public void CreateNamesAreUniqueAndRenameRejectsCollisionBeforeUndo()
        {
            _commands.CreatePackage();
            var first = _selected.Single();
            _commands.CreatePackage();
            Assert.AreEqual("DefaultPackage (1)", _selected.Single().Package.PackageName);
            int records = _undoRecords;
            Assert.Throws<InvalidOperationException>(() => _commands.Rename(_selected.Single(), first.Package.PackageName));
            Assert.AreEqual(records, _undoRecords);
            _commands.CreateGroup(first);
            _commands.CreateGroup(first);
            Assert.AreEqual("Default Group (1)", _selected.Single().Group.GroupName);
        }

        [Test]
        public void CollectorCopyAcrossPackagesIsDeepAndSamePackageRejectsDuplicate()
        {
            var source = CreateGroup();
            _commands.AddCollectors(source, new[] { _path });
            var collector = _selected.Single();
            collector.Collector.AssetTags = "source";
            _commands.Copy(new[] { collector });
            string before = JsonUtility.ToJson(_setting);
            int records = _undoRecords;
            Assert.Throws<InvalidOperationException>(() => _commands.Paste(source));
            Assert.AreEqual(before, JsonUtility.ToJson(_setting));
            Assert.AreEqual(records, _undoRecords);
            var destination = CreateGroup();
            _commands.Paste(destination);
            var clone = _selected.Single().Collector;
            Assert.AreNotSame(collector.Collector, clone);
            clone.AssetTags = "changed";
            Assert.AreEqual("source", collector.Collector.AssetTags);
            Assert.AreEqual(_path, clone.CollectPath);
        }

        [Test]
        public void PackageAndGroupClipboardDoNotShareNestedLists()
        {
            var source = CreateGroup();
            _commands.AddCollectors(source, new[] { _path });
            var package = CollectorTreeNode.ForPackage(source.Package);
            _commands.Copy(new[] { package });
            _commands.Paste(null);
            var copy = _selected.Single().Package;
            Assert.AreNotSame(source.Package.Groups, copy.Groups);
            Assert.AreNotSame(source.Group.Collectors, copy.Groups[0].Collectors);
            Assert.AreNotSame(source.Group.Collectors[0], copy.Groups[0].Collectors[0]);
            _commands.Copy(new[] { source });
            var destination = CreateGroup();
            _commands.Paste(CollectorTreeNode.ForPackage(destination.Package));
            Assert.AreNotSame(source.Group.Collectors, _selected.Single().Group.Collectors);
        }

        [Test]
        public void SamePackageCutMoveExcludesSourceAndUsesOneUndoRecord()
        {
            var source = CreateGroup();
            _commands.AddCollectors(source, new[] { _path, _secondPath });
            var collectors = _selected.ToList();
            _commands.CreateGroup(CollectorTreeNode.ForPackage(source.Package));
            var destination = _selected.Single();
            _commands.Copy(collectors, true);
            int records = _undoRecords;
            _commands.Paste(destination);
            Assert.AreEqual(records + 1, _undoRecords);
            Assert.AreEqual(0, source.Group.Collectors.Count);
            Assert.AreEqual(2, destination.Group.Collectors.Count);
            Assert.IsFalse(_commands.HasClipboard);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.AreEqual(2, _setting.Packages.Single().Groups[0].Collectors.Count);
            Assert.AreEqual(0, _setting.Packages.Single().Groups[1].Collectors.Count);
            Undo.PerformRedo();
            Assert.AreEqual(0, _setting.Packages.Single().Groups[0].Collectors.Count);
            Assert.AreEqual(2, _setting.Packages.Single().Groups[1].Collectors.Count);
        }

        [Test]
        public void OverlappingFolderSourcesRejectActualCollectedFilesAtomically()
        {
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { Root + "/Content" });
            string before = JsonUtility.ToJson(_setting);
            int records = _undoRecords;
            var error = Assert.Throws<InvalidOperationException>(() => _commands.AddCollectors(group, new[] { _path, _secondPath }));
            StringAssert.Contains("already exists", error.Message);
            Assert.AreEqual(before, JsonUtility.ToJson(_setting));
            Assert.AreEqual(records, _undoRecords);
        }

        [Test]
        public void WrongKindAndStaleCutDestinationsAreDisabled()
        {
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { _path });
            var collector = _selected.Single();
            _commands.Copy(new[] { collector }, true);
            Assert.IsFalse(_commands.CanPaste(CollectorTreeNode.ForPackage(group.Package)));
            Assert.IsFalse(_commands.CanPaste(collector));
            Assert.IsTrue(_commands.CanPaste(group));
            _commands.Remove(new[] { collector });
            Assert.IsFalse(_commands.CanPaste(group));
            Assert.Throws<InvalidOperationException>(() => _commands.Paste(group));
        }

        [Test]
        public void ReadOnlyPreviewRejectsEveryConfigurationMutation()
        {
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { Root + "/Content" });
            var owner = _selected.Single();
            var preview = new CollectorTreeNode { Kind = CollectorTreeKind.PreviewAsset, Package = owner.Package, Group = owner.Group, Collector = owner.Collector, AssetPath = _path };
            string before = JsonUtility.ToJson(_setting);
            string bytes = File.ReadAllText(_path);
            Assert.IsFalse(CollectorTreeCommands.CanEdit(new[] { preview }));
            Assert.Throws<InvalidOperationException>(() => _commands.Remove(new[] { preview }));
            Assert.Throws<InvalidOperationException>(() => _commands.Copy(new[] { preview }));
            Assert.Throws<InvalidOperationException>(() => _commands.Rename(preview, "other"));
            Assert.AreEqual(before, JsonUtility.ToJson(_setting));
            Assert.AreEqual(bytes, File.ReadAllText(_path));
            Assert.AreSame(owner.Collector, preview.Collector);
        }

        [Test]
        public void ChangePathChangesOnlyConfigurationAndSaveReloadPersistsIt()
        {
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { _path });
            var node = _selected.Single();
            _commands.ChangePath(node, _secondPath);
            Assert.AreEqual(_secondPath, node.Collector.CollectPath);
            Assert.IsTrue(File.Exists(_path));
            BundleCollectorSettingData.SaveFile();
            string serialized = File.ReadAllText(Root + "/Setting.asset");
            StringAssert.Contains(_secondPath, serialized);
            AssetDatabase.ImportAsset(Root + "/Setting.asset", ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<BundleCollectorSetting>(Root + "/Setting.asset");
            Assert.AreEqual(_secondPath, reloaded.Packages.Single().Groups.Single().Collectors.Single().CollectPath);
        }

        [Test]
        public void LayerBadgesUseCapsuleColorsAndResetWhenRowsAreRecycled()
        {
            var tree = new CollectorTreeView(selected => { }, (menu, node) => { }, (node, paths) => { }, (nodes, target) => { });
            var package = new BundleCollectorPackage { PackageName = "BadgePackage" };
            var group = new BundleCollectorGroup { GroupName = "BadgeGroup" };
            var collector = new BundleCollector { CollectPath = _path };
            var nodes = new[]
            {
                CollectorTreeNode.ForPackage(package),
                CollectorTreeNode.ForGroup(package, group),
                CollectorTreeNode.ForCollector(package, group, collector),
                new CollectorTreeNode { Kind = CollectorTreeKind.PreviewFolder, Package = package, Group = group, Collector = collector, AssetPath = Root + "/Content" },
                new CollectorTreeNode { Kind = CollectorTreeKind.PreviewAsset, Package = package, Group = group, Collector = collector, AssetPath = _path },
                new CollectorTreeNode { Kind = CollectorTreeKind.Placeholder, Package = package, Group = group, Collector = collector, AssetPath = Root + "/Content/..." }
            };
            tree.Element.SetRootItems(nodes.Select((node, index) => new TreeViewItemData<CollectorTreeNode>(index + 1, node)).ToList());
            var row = tree.Element.makeItem();
            var badge = row.Q<Label>("KindBadge");
            Assert.AreEqual(PickingMode.Ignore, badge.pickingMode);
            CollectionAssert.AreEqual(new[] { "Icon", "Name", "KindBadge", "RowSpace" }, row.Children().Select(child => child.name).ToArray());
            Assert.AreEqual(14f, badge.style.height.value.value);
            Assert.AreEqual(7f, badge.style.borderTopLeftRadius.value.value);
            Assert.AreEqual(7f, badge.style.borderBottomRightRadius.value.value);
            Assert.AreEqual(9f, badge.style.fontSize.value.value);
            Assert.AreEqual(0f, row.Q<Label>("Name").style.flexGrow.value);
            Assert.AreEqual(TextOverflow.Ellipsis, row.Q<Label>("Name").style.textOverflow.value);
            tree.Element.bindItem(row, 0);
            Assert.AreEqual("package", badge.text);
            Assert.AreEqual(DisplayStyle.Flex, badge.style.display.value);
            Assert.AreEqual(new Color(0.18f, 0.62f, 0.32f), badge.style.backgroundColor.value);
            Assert.AreEqual(Color.white, badge.style.color.value);
            tree.Element.bindItem(row, 1);
            Assert.AreEqual("group", badge.text);
            Assert.AreEqual(DisplayStyle.Flex, badge.style.display.value);
            Assert.AreEqual(Color.white, badge.style.backgroundColor.value);
            Assert.AreEqual(new Color(0.15f, 0.15f, 0.15f), badge.style.color.value);
            for (int index = 2; index < nodes.Length; index++)
            {
                tree.Element.bindItem(row, index);
                Assert.AreEqual(DisplayStyle.None, badge.style.display.value);
                Assert.AreEqual(string.Empty, badge.text);
            }
            tree.Element.bindItem(row, 0);
            Assert.AreEqual("package", badge.text);
            Assert.AreEqual(DisplayStyle.Flex, badge.style.display.value);
            Assert.AreEqual(new Color(0.18f, 0.62f, 0.32f), badge.style.backgroundColor.value);
        }

        /// <summary>All 与无后缀是不同状态，规范化不改变调用方列表。</summary>
        [Test]
        public void ExtensionsNormalizeAllNoExtensionAndRejectInvalidEntries()
        {
            Assert.IsEmpty(BundleCollector.NormalizeExtensions(null));
            Assert.IsEmpty(BundleCollector.NormalizeExtensions(Array.Empty<string>()));
            var input = new List<string> { "TXT", ".txt", " *.BYTES ", "" };
            CollectionAssert.AreEqual(new[] { "", ".bytes", ".txt" }, BundleCollector.NormalizeExtensions(input));
            CollectionAssert.AreEqual(new[] { "TXT", ".txt", " *.BYTES ", "" }, input);
            foreach (string invalid in new[] { ".", "a/b", "a\\b", "txt;bytes", "t xt", "*" })
                Assert.Throws<ArgumentException>(() => BundleCollector.NormalizeExtensions(new[] { invalid }), invalid);
            Assert.Throws<ArgumentException>(() => BundleCollector.NormalizeExtensions(new string[] { null }));
        }

        /// <summary>目录筛选递归、大小写不敏感并支持多选，单文件忽略新增白名单。</summary>
        [Test]
        public void FolderExtensionsAllNestedCaseInsensitiveMultiSelectAndSingleFile()
        {
            string bytes = WriteFixtureFile("Content/Nested/three.BYTES", "three");
            string upper = WriteFixtureFile("Content/Nested/four.TXT", "four");
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { Root + "/Content" });
            var node = _selected.Single();
            var expected = new[] { _path, _secondPath, bytes, upper };
            node.Collector.CollectExtensions = null;
            CollectionAssert.AreEquivalent(expected, CollectedPaths(node));
            node.Collector.CollectExtensions = new List<string>();
            CollectionAssert.AreEquivalent(expected, CollectedPaths(node));
            _commands.ChangeExtensions(node, new[] { "BYTES", ".bytes" });
            CollectionAssert.AreEqual(new[] { bytes }, CollectedPaths(node));
            _commands.ChangeExtensions(node, new[] { "TXT", "*.bytes" });
            CollectionAssert.AreEquivalent(expected, CollectedPaths(node));
            var file = new BundleCollector { CollectPath = _path, CollectExtensions = new List<string> { ".bytes" } };
            CollectionAssert.AreEqual(new[] { _path }, file.GetAllCollectAssets(CollectorTreeCommands.CreateCollectCommand(group.Package), group.Group).Select(info => info.AssetInfo.AssetPath).ToArray());
        }

        /// <summary>新白名单和旧 Ignore/Filter 取交集，不可复活旧规则排除的资源。</summary>
        [Test]
        public void ExtensionsIntersectLegacyFilterAndIgnoreRules()
        {
            string ignored = WriteFixtureFile("Content/Editor/hidden.txt", "hidden");
            string shaderInclude = WriteFixtureFile("Content/hidden.hlsl", "// fixture include");
            string prefab = Root + "/Content/model.prefab";
            var instance = new GameObject("ExtensionFixturePrefab");
            try { PrefabUtility.SaveAsPrefabAsset(instance, prefab); }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { Root + "/Content" });
            var node = _selected.Single();
            _commands.ChangeExtensions(node, new[] { ".txt", ".hlsl", ".prefab" });
            CollectionAssert.AreEquivalent(new[] { _path, _secondPath, prefab }, CollectedPaths(node));
            Assert.IsFalse(CollectedPaths(node).Contains(ignored));
            Assert.IsFalse(CollectedPaths(node).Contains(shaderInclude));
            node.Collector.FilterRuleName = nameof(CollectPrefab);
            CollectionAssert.AreEqual(new[] { prefab }, CollectedPaths(node));
            node.Collector.CollectExtensions = new List<string> { ".txt" };
            Assert.IsEmpty(CollectedPaths(node));
            CollectionAssert.AreEquivalent(new[] { ".prefab", ".txt" }, node.Collector.GetAvailableExtensions(CollectorTreeCommands.CreateCollectCommand(group.Package), group.Group));
        }

        /// <summary>候选后缀来自旧规则全集并保留已保存但当前不存在的值。</summary>
        [Test]
        public void AvailableExtensionsIgnoreCurrentSelectionAndPreserveSavedValues()
        {
            WriteFixtureFile("Content/Nested/three.bytes", "three");
            WriteFixtureFile("Content/Editor/hidden.json", "{}");
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { Root + "/Content" });
            var node = _selected.Single();
            var command = CollectorTreeCommands.CreateCollectCommand(group.Package);
            node.Collector.CollectExtensions = new List<string> { ".txt" };
            CollectionAssert.AreEquivalent(new[] { ".bytes", ".txt" }, node.Collector.GetAvailableExtensions(command, group.Group));
            node.Collector.CollectExtensions = new List<string> { "OBSOLETE", "" };
            CollectionAssert.AreEquivalent(new[] { "", ".bytes", ".obsolete", ".txt" }, node.Collector.GetAvailableExtensions(command, group.Group));
            Assert.IsEmpty(CollectedPaths(node));
        }

        /// <summary>真实菜单多选、勾选和显式 All；最后一项取消必须报错并保留选择。</summary>
        [Test]
        public void ExtensionMenuExecutesActionsChecksStateAndRejectsLastCancellation()
        {
            var changes = new List<List<string>>();
            var errors = new List<Exception>();
            var menu = new CollectorExtensionMenu(null, () => new List<string> { ".bytes", ".txt" }, values => changes.Add(new List<string>(values)), errors.Add);
            Assert.IsInstanceOf<ToolbarMenu>(menu.Menu);
            AssertMenuChecked(menu.Menu, "All", true);
            MenuAction(menu.Menu, ".txt").Execute();
            AssertMenuChecked(menu.Menu, ".txt", true);
            AssertMenuChecked(menu.Menu, "All", false);
            MenuAction(menu.Menu, ".bytes").Execute();
            CollectionAssert.AreEqual(new[] { ".bytes", ".txt" }, changes.Last());
            AssertMenuChecked(menu.Menu, ".bytes", true);
            MenuAction(menu.Menu, ".txt").Execute();
            AssertMenuChecked(menu.Menu, ".txt", false);
            int count = changes.Count;
            MenuAction(menu.Menu, ".bytes").Execute();
            Assert.AreEqual(count, changes.Count);
            Assert.AreEqual(1, errors.Count);
            Assert.IsInstanceOf<InvalidOperationException>(errors.Single());
            AssertMenuChecked(menu.Menu, ".bytes", true);
            AssertMenuChecked(menu.Menu, "All", false);
            Assert.AreEqual(".bytes", menu.Menu.text);
            MenuAction(menu.Menu, "All").Execute();
            Assert.IsEmpty(changes.Last());
            AssertMenuChecked(menu.Menu, "All", true);
            AssertMenuChecked(menu.Menu, ".bytes", false);
        }

        /// <summary>事务失败不得更新菜单内部状态，也不得把失败转换为 All。</summary>
        [Test]
        public void ExtensionMenuFailedCommitKeepsCheckedStateAndSavedOption()
        {
            var errors = new List<Exception>();
            var menu = new CollectorExtensionMenu(new[] { ".saved" }, () => new List<string> { ".txt" }, values => { throw new InvalidOperationException("fixture conflict"); }, errors.Add);
            AssertMenuChecked(menu.Menu, ".saved", true);
            MenuAction(menu.Menu, "All").Execute();
            Assert.AreEqual(1, errors.Count);
            AssertMenuChecked(menu.Menu, ".saved", true);
            AssertMenuChecked(menu.Menu, "All", false);
            Assert.AreEqual(".saved", menu.Menu.text);
        }

        /// <summary>冲突在 Undo 前校验，失败时配置、输入列表及文件均保持原样。</summary>
        [Test]
        public void ChangeExtensionsRejectsConflictAtomicallyAndClonesInput()
        {
            string bytes = WriteFixtureFile("Content/Nested/three.bytes", "three");
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { Root + "/Content" });
            var node = _selected.Single();
            var input = new List<string> { "TXT" };
            int records = _undoRecords;
            _commands.ChangeExtensions(node, input);
            Assert.AreEqual(records + 1, _undoRecords);
            Assert.AreNotSame(input, node.Collector.CollectExtensions);
            input.Add(".bytes");
            CollectionAssert.AreEqual(new[] { ".txt" }, node.Collector.CollectExtensions);
            _commands.AddCollectors(group, new[] { bytes });
            string before = JsonUtility.ToJson(_setting);
            records = _undoRecords;
            foreach (var values in new[] { new[] { ".bytes" }, Array.Empty<string>() })
            {
                StringAssert.Contains("already exists", Assert.Throws<InvalidOperationException>(() => _commands.ChangeExtensions(node, values)).Message);
                Assert.AreEqual(before, JsonUtility.ToJson(_setting));
                Assert.AreEqual(records, _undoRecords);
            }
            _commands.ChangeExtensions(node, new[] { "*.TXT", ".txt" });
            Assert.AreEqual(records, _undoRecords, "Equivalent canonical selection is a no-op.");
            Assert.AreEqual("one", File.ReadAllText(_path));
            Assert.AreEqual("three", File.ReadAllText(bytes));
        }

        /// <summary>后缀成功修改恰好一条 Undo；复制、保存和重载均不共享后缀列表。</summary>
        [Test]
        public void ChangeExtensionsUndoRedoDeepCopyAndSaveReload()
        {
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { Root + "/Content" });
            var node = _selected.Single();
            int records = _undoRecords;
            _commands.ChangeExtensions(node, new[] { "TXT" });
            Assert.AreEqual(records + 1, _undoRecords);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.IsEmpty(_setting.Packages.Single().Groups.Single().Collectors.Single().CollectExtensions);
            Undo.PerformRedo();
            var package = _setting.Packages.Single();
            var currentGroup = package.Groups.Single();
            node = CollectorTreeNode.ForCollector(package, currentGroup, currentGroup.Collectors.Single());
            CollectionAssert.AreEqual(new[] { ".txt" }, node.Collector.CollectExtensions);
            _commands.Copy(new[] { node });
            var destination = CreateGroup();
            _commands.Paste(destination);
            var clone = _selected.Single().Collector;
            Assert.AreNotSame(node.Collector.CollectExtensions, clone.CollectExtensions);
            clone.CollectExtensions.Add(".bytes");
            CollectionAssert.AreEqual(new[] { ".txt" }, node.Collector.CollectExtensions);
            BundleCollectorSettingData.SaveFile();
            StringAssert.Contains(".txt", File.ReadAllText(Root + "/Setting.asset"));
            AssetDatabase.ImportAsset(Root + "/Setting.asset", ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<BundleCollectorSetting>(Root + "/Setting.asset");
            CollectionAssert.AreEqual(new[] { ".txt" }, reloaded.Packages[0].Groups[0].Collectors[0].CollectExtensions);
            CollectionAssert.AreEqual(new[] { ".txt", ".bytes" }, reloaded.Packages[1].Groups[0].Collectors[0].CollectExtensions);
        }

        /// <summary>单文件、预览、错误层和失效节点不得编辑后缀，失败不留 Undo。</summary>
        [Test]
        public void ChangeExtensionsRejectsSingleFilePreviewWrongKindAndStaleNodes()
        {
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { _path });
            var file = _selected.Single();
            var preview = new CollectorTreeNode { Kind = CollectorTreeKind.PreviewAsset, Package = group.Package, Group = group.Group, Collector = file.Collector, AssetPath = _path };
            string before = JsonUtility.ToJson(_setting);
            int records = _undoRecords;
            foreach (var target in new[] { file, preview, group, CollectorTreeNode.ForPackage(group.Package) })
                Assert.Throws<InvalidOperationException>(() => _commands.ChangeExtensions(target, new[] { ".txt" }));
            Assert.AreEqual(before, JsonUtility.ToJson(_setting));
            Assert.AreEqual(records, _undoRecords);
            _commands.Remove(new[] { file });
            records = _undoRecords;
            Assert.Throws<InvalidOperationException>(() => _commands.ChangeExtensions(file, new[] { ".txt" }));
            Assert.AreEqual(records, _undoRecords);
        }

        /// <summary>XML 后缀分号、无后缀标记及 All 往返保存，所有 XML 为 fixture 独有。</summary>
        [Test]
        public void ExtensionsXmlRoundTripIncludesNoExtensionAndAll()
        {
            var group = CreateGroup();
            _commands.AddCollectors(group, new[] { Root + "/Content" });
            group.Group.Collectors.Single().CollectExtensions = new List<string> { "TXT", ".bytes", "" };
            string xml = Root + "/roundtrip.xml";
            BundleCollectorConfig.ExportXmlConfig(xml);
            var document = XDocument.Load(xml);
            Assert.AreEqual("<none>;.bytes;.txt", (string)document.Descendants("Collector").Single().Attribute("CollectExtensions"));
            BundleCollectorConfig.ImportXmlConfig(xml);
            CollectionAssert.AreEqual(new[] { "", ".bytes", ".txt" }, _setting.Packages.Single().Groups.Single().Collectors.Single().CollectExtensions);
            _setting.Packages.Single().Groups.Single().Collectors.Single().CollectExtensions = null;
            BundleCollectorConfig.ExportXmlConfig(xml);
            BundleCollectorConfig.ImportXmlConfig(xml);
            Assert.IsEmpty(_setting.Packages.Single().Groups.Single().Collectors.Single().CollectExtensions);
        }

        /// <summary>独立构造旧版 v1 缺后缀属性 XML，必须保持历史 All。</summary>
        [Test]
        public void LegacyV1XmlWithoutExtensionsDefaultsToAll()
        {
            string xml = Root + "/legacy-v1.xml";
            // 不复用生产导出器，防止相同字段 bug 相互掩盖。
            var collector = new XElement("Collector",
                new XAttribute("CollectPath", Root + "/Content"),
                new XAttribute("CollectGUID", AssetDatabase.AssetPathToGUID(Root + "/Content")),
                new XAttribute("CollectType", "MainAssetCollector"),
                new XAttribute("AddressRule", nameof(AddressByFileName)), new XAttribute("PackRule", nameof(PackDirectory)),
                new XAttribute("FilterRule", nameof(CollectAll)), new XAttribute("UserData", ""), new XAttribute("AssetTags", ""));
            new XDocument(new XElement("root", new XAttribute("Version", "1"),
                new XElement("Package", new XAttribute("PackageName", "LegacyFixture"), new XAttribute("PackageDesc", ""),
                    new XAttribute("AutoCollectShaders", false), new XAttribute("IgnoreRuleName", nameof(NormalIgnoreRule)),
                    new XElement("Group", new XAttribute("GroupActiveRule", nameof(EnableGroup)), new XAttribute("GroupName", "LegacyGroup"),
                        new XAttribute("GroupDesc", ""), new XAttribute("AssetTags", ""), collector)))).Save(xml);
            BundleCollectorConfig.ImportXmlConfig(xml);
            var package = _setting.Packages.Single();
            var group = package.Groups.Single();
            Assert.IsEmpty(group.Collectors.Single().CollectExtensions);
            CollectionAssert.AreEquivalent(new[] { _path, _secondPath }, CollectedPaths(CollectorTreeNode.ForCollector(package, group, group.Collectors.Single())));
        }

        /// <summary>混合目录与目录外工程文件可一次加入，非法批次和场景对象整体拒绝。</summary>
        [Test]
        public void MixedProjectFolderAndOutsideFileAddAndInvalidBatchStayAtomic()
        {
            string outside = WriteFixtureFile("outside.txt", "outside");
            var group = CreateGroup();
            var assets = new[] { AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(Root + "/Content"), AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(outside) };
            Assert.IsTrue(CollectorTreeCommands.CanAddProjectAssets(group, assets));
            int records = _undoRecords;
            _commands.AddCollectors(group, assets.Select(AssetDatabase.GetAssetPath));
            Assert.AreEqual(records + 1, _undoRecords);
            CollectionAssert.AreEquivalent(new[] { Root + "/Content", outside }, group.Group.Collectors.Select(c => c.CollectPath).ToArray());
            var sceneObject = new GameObject("CollectorNonProjectFixture");
            try
            {
                Assert.IsFalse(CollectorTreeCommands.CanAddProjectAssets(group, new UnityEngine.Object[] { assets[0], sceneObject }));
                Assert.IsFalse(CollectorTreeCommands.CanAddProjectAssets(group, new UnityEngine.Object[] { null }));
                Assert.IsFalse(CollectorTreeCommands.CanAddProjectAssets(group, Array.Empty<UnityEngine.Object>()));
                Assert.IsFalse(CollectorTreeCommands.CanAddProjectAssets(CollectorTreeNode.ForPackage(group.Package), assets));
                Assert.IsFalse(CollectorTreeCommands.CanAddProjectAssets(_selected.First(), assets));
            }
            finally { UnityEngine.Object.DestroyImmediate(sceneObject); }
            string validNew = WriteFixtureFile("new.txt", "new");
            string before = JsonUtility.ToJson(_setting);
            records = _undoRecords;
            Assert.Throws<ArgumentException>(() => _commands.AddCollectors(group, new[] { validNew, Root + "/missing.txt" }));
            Assert.AreEqual(before, JsonUtility.ToJson(_setting));
            Assert.AreEqual(records, _undoRecords);
            Assert.AreEqual("outside", File.ReadAllText(outside));
            Assert.AreEqual("new", File.ReadAllText(validNew));
        }

        /// <summary>创建 fixture 文件并同步导入，不覆盖已存在资源。</summary>
        private string WriteFixtureFile(string relative, string content)
        {
            string path = Root + "/" + relative;
            string folder = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
            {
                string current = Root;
                foreach (string segment in folder.Substring(Root.Length + 1).Split('/'))
                {
                    string child = current + "/" + segment;
                    if (!AssetDatabase.IsValidFolder(child)) AssetDatabase.CreateFolder(current, segment);
                    current = child;
                }
            }
            Assert.IsFalse(File.Exists(path), "Fixture must not overwrite " + path);
            File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        /// <summary>取得目录实际主资源路径，不忽略旧 Filter/Ignore。</summary>
        private static string[] CollectedPaths(CollectorTreeNode node)
        {
            return node.Collector.GetAllCollectAssets(CollectorTreeCommands.CreateCollectCommand(node.Package), node.Group).Select(info => info.AssetInfo.AssetPath).ToArray();
        }

        /// <summary>取得真实 DropdownMenuAction，不用命令替代菜单点击。</summary>
        private static DropdownMenuAction MenuAction(ToolbarMenu menu, string name)
        {
            return menu.menu.MenuItems().OfType<DropdownMenuAction>().Single(action => action.name == name);
        }

        /// <summary>刷新并断言菜单勾选状态。</summary>
        private static void AssertMenuChecked(ToolbarMenu menu, string name, bool expected)
        {
            var action = MenuAction(menu, name);
            action.UpdateActionStatus(null);
            Assert.AreEqual(expected, (action.status & DropdownMenuAction.Status.Checked) != 0, name);
        }

        private CollectorTreeNode CreateGroup()
        {
            _commands.CreatePackage();
            _commands.CreateGroup(_selected.Single());
            return _selected.Single();
        }
    }
}
#endif
