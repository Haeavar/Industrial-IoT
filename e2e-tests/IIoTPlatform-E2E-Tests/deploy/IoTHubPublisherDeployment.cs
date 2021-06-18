﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace IIoTPlatform_E2E_Tests.Deploy {
    using System;
    using System.Collections.Generic;
    using Newtonsoft.Json;
    using TestExtensions;

    public sealed class IoTHubPublisherDeployment : DeploymentConfiguration {

        /// <summary>
        /// Create deployment
        /// </summary>
        /// <param name="context"></param>
        public IoTHubPublisherDeployment(IIoTPlatformTestContext context) : base(context) {
        }

        /// <inheritdoc />
        protected override int Priority => 1;

        /// <inheritdoc />
        protected override string DeploymentName => kDeploymentName + $"{DateTime.UtcNow.Ticks}";

        protected override string TargetCondition => kTargetCondition;

        /// <inheritdoc />
        protected override IDictionary<string, IDictionary<string, object>> CreateDeploymentModules() {
            var registryCredentials = "";

            //should only be provided if the different container registry require username and password
            if (!string.IsNullOrEmpty(_context.ContainerRegistryConfig.ContainerRegistryServer) &&
                _context.ContainerRegistryConfig.ContainerRegistryServer != TestConstants.MicrosoftContainerRegistry &&
                !string.IsNullOrEmpty(_context.ContainerRegistryConfig.ContainerRegistryPassword) &&
                !string.IsNullOrEmpty(_context.ContainerRegistryConfig.ContainerRegistryUser)) {
                var registryId = _context.ContainerRegistryConfig.ContainerRegistryServer.Split('.')[0];
                registryCredentials = @"
                    ""properties.desired.runtime.settings.registryCredentials." + registryId + @""": {
                        ""address"": """ + _context.ContainerRegistryConfig.ContainerRegistryServer + @""",
                        ""password"": """ + _context.ContainerRegistryConfig.ContainerRegistryPassword + @""",
                        ""username"": """ + _context.ContainerRegistryConfig.ContainerRegistryUser + @"""
                    },
                ";
            }

            // Configure create options per os specified
            var createOptions = JsonConvert.SerializeObject(new {
                Hostname = kModuleName,
                Cmd = new[] {
                "PkiRootPath=" + TestConstants.PublishedNodesFolder + "/pki",
                "--aa",
                "--mm=PubSub",
                "--pf=" + TestConstants.PublishedNodesFullName
            },
                HostConfig = new {
                    Binds = new[] {
                    TestConstants.PublishedNodesFolder + "/:" + TestConstants.PublishedNodesFolder
                    }
                }
            }).Replace("\"", "\\\"");

            var server = string.IsNullOrEmpty(_context.ContainerRegistryConfig.ContainerRegistryServer) ?
                TestConstants.MicrosoftContainerRegistry : _context.ContainerRegistryConfig.ContainerRegistryServer;
            var ns = string.IsNullOrEmpty(_context.ContainerRegistryConfig.ImagesNamespace) ? "" :
                _context.ContainerRegistryConfig.ImagesNamespace.TrimEnd('/') + "/";
            var version = _context.ContainerRegistryConfig.ImagesTag ?? "latest";
            var image = $"{server}/{ns}iotedge/opc-publisher:{version}";
            //var image = $"trumpfdev.azurecr.io/events-and-alarms/iotedge/opc-publisher:{version}";

            // Return deployment modules object
            var content = @"
            {
                ""$edgeAgent"": {
                    " + registryCredentials + @"
                    ""properties.desired.modules." + kModuleName + @""": {
                        ""settings"": {
                            ""image"": """ + image + @""",
                            ""createOptions"": """ + createOptions + @"""
                        },
                        ""type"": ""docker"",
                        ""status"": ""running"",
                        ""restartPolicy"": ""always"",
                        ""version"": """ + (version == "latest" ? "1.0" : version) + @"""
                    }
                },
                ""$edgeHub"": {
                    ""properties.desired.routes." + kModuleName + @"ToUpstream"": ""FROM /messages/modules/" + kModuleName + @"/* INTO $upstream"",
                    ""properties.desired.routes.leafToUpstream"": ""FROM /messages/* WHERE NOT IS_DEFINED($connectionModuleId) INTO $upstream""
                }
            }";

            return JsonConvert.DeserializeObject<IDictionary<string, IDictionary<string, object>>>(content);
        }

        private const string kModuleName = "publisher_standalone";
        private const string kDeploymentName = "__default-opcpublisher-standalone";
        private const string kTargetCondition = "(tags.__type__ = 'iiotedge' AND IS_DEFINED(tags.unmanaged))";
    }
}
