﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace OpcPublisherAEE2ETests.Config
{
    public interface IOpcPlcConfig
    {
        /// <summary>
        /// Semicolon separated URLs to load published_nodes.json from OPC-PLCs
        /// </summary>
        string Urls { get; }

        /// <summary>
        /// Semicolon separated ip addresses of the OPC-PLCs
        /// </summary>
        string Ips { get; }

        /// <summary>
        /// TenantId for SP
        /// </summary>
        string TenantId { get; }

        /// <summary>
        /// Resource Group
        /// </summary>
        string ResourceGroupName { get; }

        /// <summary>
        /// Subscription Id
        /// </summary>
        string SubscriptionId { get; }
    }
}
