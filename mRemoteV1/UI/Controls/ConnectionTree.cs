﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using BrightIdeasSoftware;
using mRemoteNG.App;
using mRemoteNG.Config.Putty;
using mRemoteNG.Connection;
using mRemoteNG.Container;
using mRemoteNG.Tools;
using mRemoteNG.Tree;
using mRemoteNG.Tree.Root;


namespace mRemoteNG.UI.Controls
{
    public partial class ConnectionTree : TreeListView
    {
        private ConnectionTreeModel _connectionTreeModel;
        private readonly ConnectionTreeDragAndDropHandler _dragAndDropHandler = new ConnectionTreeDragAndDropHandler();
        private readonly PuttySessionsManager _puttySessionsManager = PuttySessionsManager.Instance;

        public ConnectionInfo SelectedNode => (ConnectionInfo) SelectedObject;

        public NodeSearcher NodeSearcher { get; private set; }

        public Func<ConnectionInfo, bool> DeletionConfirmer { get; set; } = connectionInfo => true;

        public ConnectionTreeModel ConnectionTreeModel
        {
            get { return _connectionTreeModel; }
            set
            {
                _connectionTreeModel = value;
                PopulateTreeView();
            }
        }


        public ConnectionTree()
        {
            InitializeComponent();
            SetupConnectionTreeView();
        }

        #region ConnectionTree Setup
        private void SetupConnectionTreeView()
        {
            var imageList = new StatusImageList();
            SmallImageList = imageList.GetImageList();
            AddColumns(imageList.ImageGetter);
            LinkModelToView();
            SetupDropSink();
            SetEventHandlers();
        }

        private void AddColumns(ImageGetterDelegate imageGetterDelegate)
        {
            Columns.Add(new NameColumn(imageGetterDelegate));
        }

        private void LinkModelToView()
        {
            CanExpandGetter = item =>
            {
                var itemAsContainer = item as ContainerInfo;
                return itemAsContainer?.Children.Count > 0;
            };
            ChildrenGetter = item => ((ContainerInfo)item).Children;
        }

        private void SetupDropSink()
        {
            DropSink = new SimpleDropSink
            {
                CanDropBetween = true
            };
        }

        private void SetEventHandlers()
        {
            Collapsed += (sender, args) =>
            {
                var container = args.Model as ContainerInfo;
                if (container != null)
                    container.IsExpanded = false;
            };
            Expanded += (sender, args) =>
            {
                var container = args.Model as ContainerInfo;
                if (container != null)
                    container.IsExpanded = true;
            };
            SelectionChanged += tvConnections_AfterSelect;
            CellClick += tvConnections_NodeMouseSingleClick;
            CellClick += tvConnections_NodeMouseDoubleClick;
            CellToolTipShowing += tvConnections_CellToolTipShowing;
            ModelCanDrop += _dragAndDropHandler.HandleEvent_ModelCanDrop;
            ModelDropped += _dragAndDropHandler.HandleEvent_ModelDropped;
        }

        private void PopulateTreeView()
        {
            UnregisterModelUpdateHandlers();
            SetObjects(ConnectionTreeModel.RootNodes);
            RegisterModelUpdateHandlers();
            NodeSearcher = new NodeSearcher(ConnectionTreeModel);
            ExpandPreviouslyOpenedFolders();
            ExpandRootConnectionNode();
            OpenConnectionsFromLastSession();
        }

        private void RegisterModelUpdateHandlers()
        {
            _puttySessionsManager.PuttySessionsCollectionChanged += OnPuttySessionsCollectionChanged;
            ConnectionTreeModel.CollectionChanged += HandleCollectionChanged;
            ConnectionTreeModel.PropertyChanged += HandleCollectionPropertyChanged;
        }

        private void UnregisterModelUpdateHandlers()
        {
            _puttySessionsManager.PuttySessionsCollectionChanged -= OnPuttySessionsCollectionChanged;
            ConnectionTreeModel.CollectionChanged -= HandleCollectionChanged;
            ConnectionTreeModel.PropertyChanged -= HandleCollectionPropertyChanged;
        }

        private void OnPuttySessionsCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            RefreshObjects(GetRootPuttyNodes().ToList());
        }

        private void HandleCollectionPropertyChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
        {
            //TODO for some reason property changed events are getting triggered twice for each changed property. should be just once. cant find source of duplication
            var property = propertyChangedEventArgs.PropertyName;
            if (property != "Name" && property != "OpenConnections") return;
            var senderAsConnectionInfo = sender as ConnectionInfo;
            if (senderAsConnectionInfo != null)
                RefreshObject(senderAsConnectionInfo);
        }

        private void ExpandPreviouslyOpenedFolders()
        {
            var containerList = ConnectionTreeModel.GetRecursiveChildList(GetRootConnectionNode()).OfType<ContainerInfo>();
            var previouslyExpandedNodes = containerList.Where(container => container.IsExpanded);
            ExpandedObjects = previouslyExpandedNodes;
            this.InvokeRebuildAll(true);
        }

        private void OpenConnectionsFromLastSession()
        {
            if (!Settings.Default.OpenConsFromLastSession || Settings.Default.NoReconnect) return;
            var connectionInfoList = GetRootConnectionNode().GetRecursiveChildList().Where(node => !(node is ContainerInfo));
            var previouslyOpenedConnections = connectionInfoList.Where(item => item.PleaseConnect);
            foreach (var connectionInfo in previouslyOpenedConnections)
            {
                ConnectionInitiator.OpenConnection(connectionInfo);
            }
        }
        #endregion

        #region ConnectionTree Behavior
        public void DeleteSelectedNode()
        {
            if (SelectedNode is RootNodeInfo || SelectedNode is PuttySessionInfo) return;
            if (!DeletionConfirmer(SelectedNode)) return;
            ConnectionTreeModel.DeleteNode(SelectedNode);
            Runtime.SaveConnectionsAsync();
        }

        private void HandleCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            var senderAsContainerInfo = sender as ContainerInfo;
            // ReSharper disable once SwitchStatementMissingSomeCases
            switch (args?.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    var childList = senderAsContainerInfo?.Children;
                    ConnectionInfo otherChild = null;
                    if (childList?.Count > 1)
                        otherChild = childList.First(child => !args.NewItems.Contains(child));
                    RefreshObject(otherChild ?? senderAsContainerInfo);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    if (senderAsContainerInfo?.Children.Count > 0)
                        RefreshObjects(args.OldItems);
                    else
                        RefreshObject(senderAsContainerInfo);
                    break;
                case NotifyCollectionChangedAction.Move:
                    RefreshObjects(args.OldItems);
                    break;
                case NotifyCollectionChangedAction.Reset:
                    RefreshObject(senderAsContainerInfo);
                    break;
                case NotifyCollectionChangedAction.Replace:
                    break;
                case null:
                    break;
            }
        }

        public void AddConnection()
        {
            try
            {
                AddNode(new ConnectionInfo());
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("UI.Window.Tree.AddConnection() failed.", ex);
            }
        }

        public void AddFolder()
        {
            try
            {
                AddNode(new ContainerInfo());
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.strErrorAddFolderFailed, ex);
            }
        }

        private void AddNode(ConnectionInfo newNode)
        {
            if (SelectedNode == null) return;
            DefaultConnectionInfo.Instance.SaveTo(newNode);
            DefaultConnectionInheritance.Instance.SaveTo(newNode.Inheritance);
            var selectedContainer = SelectedNode as ContainerInfo;
            var parent = selectedContainer ?? SelectedNode?.Parent;
            newNode.SetParent(parent);
            Expand(parent);
            SelectObject(newNode);
            EnsureModelVisible(newNode);
        }

        public void DisconnectConnection(ConnectionInfo connectionInfo)
        {
            try
            {
                if (connectionInfo == null) return;
                var nodeAsContainer = connectionInfo as ContainerInfo;
                if (nodeAsContainer != null)
                {
                    foreach (var child in nodeAsContainer.Children)
                    {
                        for (var i = 0; i <= child.OpenConnections.Count - 1; i++)
                        {
                            child.OpenConnections[i].Disconnect();
                        }
                    }
                }
                else
                {
                    for (var i = 0; i <= connectionInfo.OpenConnections.Count - 1; i++)
                    {
                        connectionInfo.OpenConnections[i].Disconnect();
                    }
                }
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("DisconnectConnection (UI.Window.ConnectionTreeWindow) failed", ex);
            }
        }

        public void SshTransferFile()
        {
            try
            {
                Windows.Show(WindowType.SSHTransfer);
                Windows.SshtransferForm.Hostname = SelectedNode.Hostname;
                Windows.SshtransferForm.Username = SelectedNode.Username;
                Windows.SshtransferForm.Password = SelectedNode.Password;
                Windows.SshtransferForm.Port = Convert.ToString(SelectedNode.Port);
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("SSHTransferFile (UI.Window.ConnectionTreeWindow) failed", ex);
            }
        }

        public void StartExternalApp(ExternalTool externalTool)
        {
            try
            {
                if (SelectedNode.GetTreeNodeType() == TreeNodeType.Connection | SelectedNode.GetTreeNodeType() == TreeNodeType.PuttySession)
                    externalTool.Start(SelectedNode);
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("cMenTreeToolsExternalAppsEntry_Click failed (UI.Window.ConnectionTreeWindow)", ex);
            }
        }

        private void ExpandRootConnectionNode()
        {
            var rootConnectionNode = GetRootConnectionNode();
            this.InvokeExpand(rootConnectionNode);
        }

        private void tvConnections_AfterSelect(object sender, EventArgs e)
        {
            try
            {
                Windows.ConfigForm.SelectedTreeNode = SelectedNode;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("tvConnections_AfterSelect (UI.Window.ConnectionTreeWindow) failed", ex);
            }
        }

        private void tvConnections_NodeMouseSingleClick(object sender, CellClickEventArgs e)
        {
            try
            {
                if (e.ClickCount > 1) return;
                var clickedNode = e.Model as ConnectionInfo;

                if (clickedNode == null) return;
                if (clickedNode.GetTreeNodeType() != TreeNodeType.Connection && clickedNode.GetTreeNodeType() != TreeNodeType.PuttySession) return;
                if (Settings.Default.SingleClickOnConnectionOpensIt)
                    ConnectionInitiator.OpenConnection(SelectedNode);

                if (Settings.Default.SingleClickSwitchesToOpenConnection)
                    ConnectionInitiator.SwitchToOpenConnection(SelectedNode);
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("tvConnections_NodeMouseClick (UI.Window.ConnectionTreeWindow) failed", ex);
            }
        }

        private void tvConnections_NodeMouseDoubleClick(object sender, CellClickEventArgs e)
        {
            if (e.ClickCount < 2) return;
            var clickedNodeAsContainer = e.Model as ContainerInfo;
            if (clickedNodeAsContainer != null)
            {
                ToggleExpansion(clickedNodeAsContainer);
            }

            var clickedNode = e.Model as ConnectionInfo;
            if (clickedNode?.GetTreeNodeType() == TreeNodeType.Connection |
                clickedNode?.GetTreeNodeType() == TreeNodeType.PuttySession)
            {
                ConnectionInitiator.OpenConnection(SelectedNode);
            }
        }

        private void tvConnections_CellToolTipShowing(object sender, ToolTipShowingEventArgs e)
        {
            try
            {
                var nodeProducingTooltip = (ConnectionInfo)e.Model;
                e.Text = nodeProducingTooltip.Description;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("tvConnections_MouseMove (UI.Window.ConnectionTreeWindow) failed", ex);
            }
        }

        public RootNodeInfo GetRootConnectionNode()
        {
            return (RootNodeInfo)Roots.Cast<ConnectionInfo>().First(item => item is RootNodeInfo);
        }

        public IEnumerable<RootPuttySessionsNodeInfo> GetRootPuttyNodes()
        {
            return Objects.OfType<RootPuttySessionsNodeInfo>();
        }

        public void DuplicateSelectedNode()
        {
            var newNode = SelectedNode.Clone();
            newNode.Parent.SetChildBelow(newNode, SelectedNode);
            Runtime.SaveConnectionsAsync();
        }

        public void RenameSelectedNode()
        {
            SelectedItem.BeginEdit();
            Runtime.SaveConnectionsAsync();
        }
        #endregion
    }
}