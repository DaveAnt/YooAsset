#if UNITY_2022_2_OR_NEWER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
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

        private CollectorTreeNode CreateGroup()
        {
            _commands.CreatePackage();
            _commands.CreateGroup(_selected.Single());
            return _selected.Single();
        }
    }
}
#endif
