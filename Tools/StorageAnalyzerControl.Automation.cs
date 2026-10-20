using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace KillerShell.Tools
{
    internal sealed partial class StorageAnalyzerControl
    {
        private bool _accessibilityClosed;
        private void SelectNode(FsNode? node)
        {
            var previous = _selected;
            _selected = node;
            _hover = null;
            UpdateFooterLeft();
            PinSelectionToWindowStatus(node);
            _map.InvalidateOverlay();
            if (UIElementAutomationPeer.FromElement(_map) is StorageTreePeer peer)
                peer.SelectionChanged(previous, node);
        }

        private void SelectChild(FsNode parent)
        {
            if (_scanning) return;
            ZoomTo(parent);
            SelectNode(parent.Children?.FirstOrDefault() ?? parent);
        }

        private void SelectSibling(int direction)
        {
            if (_scanning || _zoomRoot == null) return;
            var siblings = _selected?.Parent?.Children ?? _zoomRoot.Children;
            if (siblings == null || siblings.Count == 0) { SelectNode(_zoomRoot); return; }
            int index = _selected == null ? -1 : siblings.IndexOf(_selected);
            int next = index < 0 ? (direction > 0 ? 0 : siblings.Count - 1) : Math.Max(0, Math.Min(siblings.Count - 1, index + direction));
            if (siblings[next].Parent is { } parent && !ReferenceEquals(parent, _zoomRoot)) ZoomTo(parent);
            SelectNode(siblings[next]);
        }

        private bool IsCurrentNode(FsNode node)
        {
            if (_scanning || _accessibilityClosed) return false;
            for (var current = node; current != null; current = current.Parent)
            {
                if (ReferenceEquals(current, _root)) return true;
                if (current.Parent?.Children?.Contains(current) != true) return false;
            }
            return false;
        }

        private static bool ContainsNode(FsNode ancestor, FsNode? node)
        {
            for (var current = node; current != null; current = current.Parent)
                if (ReferenceEquals(ancestor, current)) return true;
            return false;
        }

        private void StorageAutomationChanged()
        {
            if (UIElementAutomationPeer.FromElement(_map) is StorageTreePeer peer) peer.TreeChanged();
        }

        private void StorageAutomationViewChanged(FsNode? previousRoot)
        {
            if (UIElementAutomationPeer.FromElement(_map) is StorageTreePeer peer) peer.ViewChanged(previousRoot);
        }

        private sealed partial class TreemapSurface
        {
            protected override AutomationPeer OnCreateAutomationPeer() => new StorageTreePeer(this, _owner);
        }

        private sealed class StorageTreePeer : FrameworkElementAutomationPeer, ISelectionProvider
        {
            private readonly StorageAnalyzerControl _control;
            private readonly Dictionary<FsNode, StorageNodePeer> _nodes = new();
            internal StorageTreePeer(TreemapSurface surface, StorageAnalyzerControl control) : base(surface) => _control = control;
            protected override string GetClassNameCore() => "StorageTreemap";
            protected override string GetNameCore() => Shell.MainWindow.LocStatic("Str_TabTitle_Storage");
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;
            public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Selection ? this : base.GetPattern(pattern);
            protected override List<AutomationPeer> GetChildrenCore() => _control._scanning || _control._accessibilityClosed || _control._root == null
                ? new List<AutomationPeer>() : new List<AutomationPeer> { Node(_control._root) };
            internal StorageNodePeer Node(FsNode node)
            {
                if (!_nodes.TryGetValue(node, out var peer)) _nodes[node] = peer = new StorageNodePeer(this, _control, node);
                return peer;
            }
            internal void TreeChanged()
            {
                _nodes.Clear();
                ResetChildrenCache();
                InvalidatePeer();
                RaiseAutomationEvent(AutomationEvents.StructureChanged);
                RaiseAutomationEvent(AutomationEvents.SelectionPatternOnInvalidated);
            }
            internal void SelectionChanged(FsNode? previous, FsNode? selected)
            {
                if (ReferenceEquals(previous, selected)) return;
                if (previous != null && _nodes.TryGetValue(previous, out var old))
                    old.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, true, false);
                if (selected != null)
                {
                    var current = Node(selected);
                    current.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, false, true);
                    current.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
                    if (_control.IsKeyboardFocusWithin) current.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
                }
            }
            internal void ViewChanged(FsNode? previousRoot)
            {
                // Resetting a cached subtree can realize children synchronously.
                foreach (var entry in _nodes.ToArray())
                {
                    entry.Value.ResetChildrenCache();
                    entry.Value.InvalidatePeer();
                    if (entry.Key.IsDir && entry.Key.Children?.Count > 0)
                    {
                        var before = ContainsNode(entry.Key, previousRoot) ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
                        var after = ContainsNode(entry.Key, _control._zoomRoot) ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
                        if (before != after)
                        {
                            entry.Value.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, before, after);
                            entry.Value.RaiseAutomationEvent(AutomationEvents.StructureChanged);
                        }
                    }
                }
                InvalidatePeer();
            }
            public bool CanSelectMultiple => false;
            public bool IsSelectionRequired => false;
            public IRawElementProviderSimple[] GetSelection() => _control._selected != null && _control.IsCurrentNode(_control._selected)
                ? new[] { ProviderFromPeer(Node(_control._selected)) } : Array.Empty<IRawElementProviderSimple>();
        }

        private sealed class StorageNodePeer : AutomationPeer, ISelectionItemProvider, IExpandCollapseProvider, IInvokeProvider, IScrollItemProvider
        {
            private readonly StorageTreePeer _tree;
            private readonly StorageAnalyzerControl _control;
            private readonly FsNode _node;
            internal StorageNodePeer(StorageTreePeer tree, StorageAnalyzerControl control, FsNode node) { _tree = tree; _control = control; _node = node; }
            private void EnsureCurrent() { if (!_control.IsCurrentNode(_node)) throw new ElementNotAvailableException(); }
            protected override List<AutomationPeer> GetChildrenCore() => _control.IsCurrentNode(_node) && ContainsNode(_node, _control._zoomRoot) && _node.Children != null
                ? _node.Children.Select(child => (AutomationPeer)_tree.Node(child)).ToList() : new List<AutomationPeer>();
            public override object? GetPattern(PatternInterface pattern) => pattern switch
            {
                PatternInterface.SelectionItem => this,
                PatternInterface.ExpandCollapse when _node.IsDir => this,
                PatternInterface.Invoke => this,
                PatternInterface.ScrollItem => this,
                _ => null
            };
            protected override Rect GetBoundingRectangleCore()
            {
                if (!_control.IsCurrentNode(_node) || _node.Gen != _control._gen || _node.Rect.Width <= 0 || _node.Rect.Height <= 0 || !_control._map.IsVisible) return Rect.Empty;
                return new Rect(_control._map.PointToScreen(_node.Rect.TopLeft), _control._map.PointToScreen(_node.Rect.BottomRight));
            }
            protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;
            protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
            protected override string GetItemTypeCore() => Shell.MainWindow.LocStatic(_node.IsDir ? "Str_Details_Folder" : "Str_Details_NoExt");
            protected override string GetClassNameCore() => "StorageNode";
            protected override string GetItemStatusCore() => _node.Size.ToString(CultureInfo.CurrentCulture);
            protected override bool IsRequiredForFormCore() => false;
            protected override bool IsKeyboardFocusableCore() => _control.IsCurrentNode(_node);
            protected override bool HasKeyboardFocusCore() => IsSelected && _control.IsKeyboardFocusWithin;
            protected override bool IsEnabledCore() => _control.IsEnabled && _control.IsCurrentNode(_node);
            protected override bool IsPasswordCore() => false;
            protected override string GetAutomationIdCore() => FullPath(_node);
            protected override string GetNameCore() => _node.Name + ", " + FormatSize(_node.Size);
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;
            protected override bool IsContentElementCore() => true;
            protected override bool IsControlElementCore() => true;
            protected override AutomationPeer? GetLabeledByCore() => null;
            protected override string GetHelpTextCore() => FullPath(_node) + ", " + _node.Size.ToString(CultureInfo.CurrentCulture) + " B";
            protected override string GetAcceleratorKeyCore() => _node.IsDir ? "Enter" : "Shift+Enter";
            protected override string GetAccessKeyCore() => "";
            protected override Point GetClickablePointCore()
            {
                var bounds = GetBoundingRectangleCore();
                return bounds.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            }
            protected override void SetFocusCore()
            {
                Select();
                if (_control._map.Focus()) RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
            }
            public bool IsSelected => ReferenceEquals(_control._selected, _node) && _control.IsCurrentNode(_node);
            public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(_tree);
            public void Select() { EnsureCurrent(); ScrollIntoView(); _control.SelectNode(_node); }
            public void AddToSelection() => Select();
            public void RemoveFromSelection() { EnsureCurrent(); if (IsSelected) _control.SelectNode(null); }
            public ExpandCollapseState ExpandCollapseState => !_node.IsDir || _node.Children?.Count == 0 ? ExpandCollapseState.LeafNode
                : ContainsNode(_node, _control._zoomRoot) ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
            public void Expand() { EnsureCurrent(); if (_node.IsDir) { _control.ZoomTo(_node); _control.SelectNode(_node); } }
            public void Collapse() { EnsureCurrent(); if (_node.Parent != null) { _control.ZoomTo(_node.Parent); _control.SelectNode(_node); } }
            public void Invoke() { EnsureCurrent(); if (_node.IsDir) Expand(); else _control.OpenNodeInTab(_node); }
            public void ScrollIntoView()
            {
                EnsureCurrent();
                var parent = _node.Parent ?? _node;
                if (!ReferenceEquals(parent, _control._zoomRoot)) _control.ZoomTo(parent);
            }
        }
    }
}
