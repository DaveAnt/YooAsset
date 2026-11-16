using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace YooAsset.Editor
{
    /// <summary>目录收集器后缀选择；All 为独立状态，不会因取消最后一项而悄悄扩大收集范围。</summary>
    internal sealed class CollectorExtensionMenu : VisualElement
    {
        private readonly Func<List<string>> _available;
        private readonly Action<List<string>> _changed;
        private readonly Action<Exception> _failed;
        private readonly ToolbarMenu _menu;
        private List<string> _selected;

        internal ToolbarMenu Menu
        {
            get { return _menu; }
        }

        internal CollectorExtensionMenu(IEnumerable<string> selected, Func<List<string>> available, Action<List<string>> changed, Action<Exception> failed)
        {
            _selected = BundleCollector.NormalizeExtensions(selected);
            _available = available;
            _changed = changed;
            _failed = failed;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            var label = new Label("File Extensions");
            label.style.minWidth = 120;
            Add(label);
            _menu = new ToolbarMenu { name = "CollectorExtensions" };
            _menu.style.flexGrow = 1;
            Add(_menu);
            UpdateText();
            RegisterCallback<PointerDownEvent>(evt => RefreshOptions(), TrickleDown.TrickleDown);
            RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == UnityEngine.KeyCode.Space || evt.keyCode == UnityEngine.KeyCode.Return) RefreshOptions(); }, TrickleDown.TrickleDown);
            RefreshOptions();
        }

        private void RefreshOptions()
        {
            try
            {
                _menu.menu.MenuItems().Clear();
                _menu.menu.AppendAction("All", action => Apply(new List<string>()), action => _selected.Count == 0 ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
                _menu.menu.AppendSeparator();
                foreach (string extension in _available().Concat(_selected).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.Ordinal))
                {
                    string value = extension;
                    _menu.menu.AppendAction(value.Length == 0 ? "(no extension)" : value, action => Toggle(value),
                        action => _selected.Contains(value, StringComparer.OrdinalIgnoreCase) ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
                }
            }
            catch (Exception error) { _failed(error); }
        }

        private void Toggle(string value)
        {
            var next = new List<string>(_selected);
            if (next.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                if (next.Count == 1) { _failed(new InvalidOperationException("Select at least one file extension, or choose All explicitly.")); return; }
                next.RemoveAll(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
            }
            else next.Add(value);
            Apply(next);
        }

        private void Apply(List<string> values)
        {
            try
            {
                values = BundleCollector.NormalizeExtensions(values);
                _changed(values);
                _selected = values;
                UpdateText();
            }
            catch (Exception error) { _failed(error); }
        }

        private void UpdateText()
        {
            _menu.text = _selected.Count == 0 ? "All" : string.Join(", ", _selected.Select(value => value.Length == 0 ? "(no extension)" : value));
            _menu.tooltip = "Filters directly collected folder assets. Referenced dependencies are still included in bundles.";
        }
    }
}
