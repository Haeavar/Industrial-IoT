// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace IIoTPlatformE2ETests.Deploy
{
    using Microsoft.Azure.Devices;
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using TestExtensions;

    public abstract class DeploymentConfiguration : IIoTHubEdgeDeployment
    {
        protected DeploymentConfiguration(IIoTPlatformTestContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <inheritdoc />
        public async Task<bool> CreateOrUpdateLayeredDeploymentAsync(CancellationToken token)
        {
            var deploymentConfiguration = GetDeploymentConfiguration();

            var configuration = await _context.RegistryHelper
                .CreateOrUpdateConfigurationAsync(deploymentConfiguration, token).ConfigureAwait(false);

            _context.OutputHelper.WriteLine($"Created deployment {configuration.Id}.");
            return configuration != null;
        }

        /// <inheritdoc />
        public async Task DeleteLayeredDeploymentAsync(CancellationToken token)
        {
            var deploymentConfiguration = GetDeploymentConfiguration();

            await _context.RegistryHelper
                .DeleteConfigurationAsync(deploymentConfiguration.Id, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Configuration GetDeploymentConfiguration()
        {
            return new Configuration(DeploymentName)
            {
                Content = new ConfigurationContent
                {
                    ModulesContent = CreateDeploymentModules()
                },
                TargetCondition = TargetCondition,
                Priority = Priority
            };
        }

        protected readonly IIoTPlatformTestContext _context;

        /// <summary>
        /// Create a deployment modules object
        /// </summary>
        protected abstract IDictionary<string, IDictionary<string, object>> CreateDeploymentModules();

        /// <summary>
        /// The desired rank of deployment
        /// </summary>
        protected abstract int Priority { get; }

        /// <summary>
        /// Identifier of deployment
        /// </summary>
        protected abstract string DeploymentName { get; }

        /// <summary>
        /// Target condition for applying the deployment
        /// </summary>
        protected abstract string TargetCondition { get; }
    }
}
