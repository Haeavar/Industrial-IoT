/* ========================================================================
 * Copyright (c) 2005-2017 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

namespace Views
{
    using Opc.Ua;
    using Opc.Ua.Server;
    using System.Collections.Generic;
    using System.Reflection;

    /// <summary>
    /// A node manager for a server that exposes several variables.
    /// </summary>
    public class ViewsNodeManager : CustomNodeManager2
    {
        /// <summary>
        /// Initializes the node manager.
        /// </summary>
        /// <param name="server"></param>
        /// <param name="configuration"></param>
        public ViewsNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, Model.Namespaces.Views,
                Model.Namespaces.Engineering, Model.Namespaces.Operations)
        {
            SystemContext.NodeIdFactory = this;

            // get the configuration for the node manager.
            // use suitable defaults if no configuration exists.
            _configuration = configuration.ParseExtension<ViewsServerConfiguration>()
                ?? new ViewsServerConfiguration();
        }

        /// <summary>
        /// An overrideable version of the Dispose.
        /// </summary>
        /// <param name="disposing"></param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // TBD
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Creates the NodeId for the specified node.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="node"></param>
        public override NodeId New(ISystemContext context, NodeState node)
        {
            if (node is BaseInstanceState instance && instance.Parent != null)
            {
                var pnd = ParsedNodeId.Parse(instance.Parent.NodeId);

                if (pnd != null)
                {
                    return pnd.Construct(instance.SymbolicName);
                }
            }

            return node.NodeId;
        }

        /// <summary>
        /// Loads a node set from a file or resource and addes them to the set of predefined nodes.
        /// </summary>
        /// <param name="context"></param>
        protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
        {
            var type = GetType().GetTypeInfo();
            var predefinedNodes = new NodeStateCollection();
            predefinedNodes.LoadFromBinaryResource(context,
                $"{type.Assembly.GetName().Name}.Generated.{type.Namespace}.Design.Model.PredefinedNodes.uanodes",
                type.Assembly, true);
            return predefinedNodes;
        }

        /// <summary>
        /// Does any initialization required before the address space can be used.
        /// </summary>
        /// <param name="externalReferences"></param>
        /// <remarks>
        /// The externalReferences is an out parameter that allows the node manager to link to nodes
        /// in other node managers. For example, the 'Objects' node is managed by the CoreNodeManager and
        /// should have a reference to the root folder node(s) exposed by this node manager.
        /// </remarks>
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                base.CreateAddressSpace(externalReferences);

                var root = FindPredefinedNode(new NodeId(Model.Objects.Plant, NamespaceIndex), typeof(NodeState));

                var boiler1 = new Model.BoilerState(null);
                var pnd1 = new ParsedNodeId { NamespaceIndex = NamespaceIndex, RootId = "Boiler #1" };

                boiler1.Create(
                    SystemContext,
                    pnd1.Construct(),
                    new QualifiedName("Boiler #1", NamespaceIndex),
                    null,
                    true);

                boiler1.AddReference(ReferenceTypeIds.Organizes, true, root.NodeId);
                root.AddReference(ReferenceTypeIds.Organizes, false, boiler1.NodeId);

                AddPredefinedNode(SystemContext, boiler1);

                var boiler2 = new Model.BoilerState(null);
                var pnd2 = new ParsedNodeId { NamespaceIndex = NamespaceIndex, RootId = "Boiler #2" };

                boiler2.Create(
                    SystemContext,
                    pnd2.Construct(),
                    new QualifiedName("Boiler #2", NamespaceIndex),
                    null,
                    true);

                boiler2.AddReference(ReferenceTypeIds.Organizes, true, root.NodeId);
                root.AddReference(ReferenceTypeIds.Organizes, false, boiler2.NodeId);

                AddPredefinedNode(SystemContext, boiler2);
            }
        }

        /// <summary>
        /// Checks if the node is in the view.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="continuationPoint"></param>
        /// <param name="node"></param>
        protected override bool IsNodeInView(ServerSystemContext context, ContinuationPoint continuationPoint, NodeState node)
        {
            if (continuationPoint.View != null)
            {
                if (continuationPoint.View.ViewId == new NodeId(Model.Views.Engineering, NamespaceIndex))
                {
                    // suppress operations properties.
                    if (node != null && node.BrowseName.NamespaceIndex == NamespaceIndexes[2])
                    {
                        return false;
                    }
                }

                if (continuationPoint.View.ViewId == new NodeId(Model.Views.Operations, NamespaceIndex))
                {
                    // suppress engineering properties.
                    if (node != null && node.BrowseName.NamespaceIndex == NamespaceIndexes[1])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Checks if the reference is in the view.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="continuationPoint"></param>
        /// <param name="reference"></param>
        protected override bool IsReferenceInView(ServerSystemContext context, ContinuationPoint continuationPoint, IReference reference)
        {
            if (continuationPoint.View != null)
            {
                // guard against absolute node ids.
                if (reference.TargetId.IsAbsolute)
                {
                    return true;
                }

                // find the node.
                var node = FindPredefinedNode((NodeId)reference.TargetId, typeof(NodeState));

                if (node != null)
                {
                    return IsNodeInView(context, continuationPoint, node);
                }
            }

            return true;
        }

        /// <summary>
        /// Frees any resources allocated for the address space.
        /// </summary>
        public override void DeleteAddressSpace()
        {
        }

        /// <summary>
        /// Returns a unique handle for the node.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="nodeId"></param>
        /// <param name="cache"></param>
        protected override NodeHandle GetManagerHandle(ServerSystemContext context, NodeId nodeId, IDictionary<NodeId, NodeState> cache)
        {
            lock (Lock)
            {
                // quickly exclude nodes that are not in the namespace.
                if (!IsNodeIdInNamespace(nodeId))
                {
                    return null;
                }

                // check cache (the cache is used because the same node id can appear many times in a single request).
                if (cache != null && cache.TryGetValue(nodeId, out var node))
                {
                    return new NodeHandle(nodeId, node);
                }

                // look up predefined node.
                if (PredefinedNodes.TryGetValue(nodeId, out node))
                {
                    var handle = new NodeHandle(nodeId, node);

                    cache?.Add(nodeId, node);

                    return handle;
                }
                return null;
            }
        }

        /// <summary>
        /// Verifies that the specified node exists.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="handle"></param>
        /// <param name="cache"></param>
        protected override NodeState ValidateNode(
            ServerSystemContext context,
            NodeHandle handle,
            IDictionary<NodeId, NodeState> cache)
        {
            // not valid if no root.
            if (handle == null)
            {
                return null;
            }

            // check if previously validated.
            if (handle.Validated)
            {
                return handle.Node;
            }

            // lookup in operation cache.
            var target = FindNodeInCache(context, handle, cache);

            if (target == null)
            {
                // TBD
                return null;
            }

            return ValidationComplete(context, handle, target, cache);
        }

#pragma warning disable IDE0052 // Remove unread private members
        private readonly ViewsServerConfiguration _configuration;
#pragma warning restore IDE0052 // Remove unread private members
    }
}
