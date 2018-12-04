// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Agent.Service.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Autofac;
    using k8s;
    using global::Docker.DotNet;
    using global::Docker.DotNet.Models;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes;
    using Microsoft.Azure.Devices.Edge.Agent.IoTHub;
    using Microsoft.Azure.Devices.Edge.Storage;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Extensions.Logging;
    using Microsoft.Azure.Devices.Edge.Agent.Docker;
    using Microsoft.Azure.Devices.Edge.Agent.Edgelet;
    using ModuleIdentityLifecycleManager = Microsoft.Azure.Devices.Edge.Agent.Edgelet.ModuleIdentityLifecycleManager;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes.Planners;
    using Microsoft.Azure_Devices.Edge.Agent.Kubernetes;

    public class KubernetesModule : Module
    {
        readonly string deviceId;
        readonly string iotHubHostname;
        readonly string gatewayHostname;
        readonly Uri managementUri;
        readonly Uri workloadUri;
        readonly IEnumerable<AuthConfig> dockerAuthConfig;
        readonly Option<UpstreamProtocol> upstreamProtocol;
        readonly Option<string> productInfo;

        public KubernetesModule(string iotHubHostname, string gatewayHostName, string deviceId, Uri managementUri,
            Uri workloadUri, IEnumerable<AuthConfig> dockerAuthConfig, Option<UpstreamProtocol> upstreamProtocol, Option<string> productInfo)
        {

            this.iotHubHostname = Preconditions.CheckNonWhiteSpace(iotHubHostname, nameof(iotHubHostname));
            this.gatewayHostname = Preconditions.CheckNonWhiteSpace(gatewayHostName, nameof(gatewayHostName));
            this.deviceId = Preconditions.CheckNonWhiteSpace(deviceId, nameof(deviceId));
            this.managementUri = Preconditions.CheckNotNull(managementUri, nameof(managementUri));
            this.workloadUri = Preconditions.CheckNotNull(workloadUri, nameof(workloadUri));
            this.dockerAuthConfig = Preconditions.CheckNotNull(dockerAuthConfig, nameof(dockerAuthConfig));
            this.upstreamProtocol = Preconditions.CheckNotNull(upstreamProtocol, nameof(upstreamProtocol));
            this.productInfo = productInfo;
        }

        protected override void Load(ContainerBuilder builder)
        {
            // IKubernetesClient
            builder.Register(c =>
                {
                    //var config = new KubernetesClientConfiguration { Host = "http://127.0.0.1:35456/" };
                    //KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
                    KubernetesClientConfiguration config = KubernetesClientConfiguration.InClusterConfig();
                    var client = new Kubernetes(config);
                    return client;
                })
                .As<IKubernetes>()
                .SingleInstance();

            // IModuleClientProvider
            builder.Register(c => new EnvironmentModuleClientProvider(this.upstreamProtocol, this.productInfo))
                .As<IModuleClientProvider>()
                .SingleInstance();

            // IModuleManager
            builder.Register(c => new ModuleManagementHttpClient(this.managementUri))
                .As<IModuleManager>()
                .As<IIdentityManager>()
                .SingleInstance();

            // IModuleIdentityLifecycleManager
            var identityBuilder = new ModuleIdentityProviderServiceBuilder(this.iotHubHostname, this.deviceId, this.gatewayHostname);
            builder.Register(c => new ModuleIdentityLifecycleManager(c.Resolve<IIdentityManager>(), identityBuilder, this.workloadUri))
                .As<IModuleIdentityLifecycleManager>()
                .SingleInstance();

            // ICombinedConfigProvider<CombinedDockerConfig>
            builder.Register(
                    async c =>
                    {
                        IConfigSource configSource = await c.Resolve<Task<IConfigSource>>();
                        return new CombinedKubernetesConfigProvider(this.dockerAuthConfig, configSource) as ICombinedConfigProvider<CombinedDockerConfig>;
                    })
                .As<Task<ICombinedConfigProvider<CombinedDockerConfig>>>()
                .SingleInstance();

            // ICommandFactory
            builder.Register(
                    async c =>
                    {
                        var client = c.Resolve<IKubernetes>();
                        var configSourceTask = c.Resolve<Task<IConfigSource>>();
                        var combinedDockerConfigProviderTask = c.Resolve<Task<ICombinedConfigProvider<CombinedDockerConfig>>>();
                        IConfigSource configSource = await configSourceTask;
                        ICombinedConfigProvider<CombinedDockerConfig> combinedDockerConfigProvider = await combinedDockerConfigProviderTask;
                        var kubernetesCommandFactory = new KubernetesCommandFactory(this.iotHubHostname, this.gatewayHostname, this.deviceId, client, configSource, combinedDockerConfigProvider);
                        return new LoggingCommandFactory(kubernetesCommandFactory, c.Resolve<ILoggerFactory>()) as ICommandFactory;
                    })
                .As<Task<ICommandFactory>>()
                .SingleInstance();

            // IPlanner
            builder.Register(async c =>
                {
                    var commandFactoryTask = c.Resolve<Task<ICommandFactory>>();
                    var combinedConfigProviderTask = c.Resolve<Task<ICombinedConfigProvider<CombinedDockerConfig>>>();
                    ICommandFactory commandFactory = await commandFactoryTask;
                    ICombinedConfigProvider<CombinedDockerConfig> combinedConfigProvider = await combinedConfigProviderTask;
                    return new KubernetesPlanner<CombinedDockerConfig>(this.iotHubHostname, this.gatewayHostname, this.deviceId, c.Resolve<IKubernetes>(), commandFactory, combinedConfigProvider) as IPlanner;
                })
                .As<Task<IPlanner>>()
                .SingleInstance();

            // IRuntimeInfoProvider, IKubernetesOperator
            builder.Register(
                    c => Task.FromResult(new EdgeOperator(this.iotHubHostname, this.deviceId, this.gatewayHostname, c.Resolve<IKubernetes>()) as IRuntimeInfoProvider))
                .As<Task<IRuntimeInfoProvider>>()
                .SingleInstance();

            // Task<IEnvironmentProvider>
            builder.Register(
                async c =>
                {
                    var moduleStateStore = c.Resolve<IEntityStore<string, ModuleState>>();
                    var restartPolicyManager = c.Resolve<IRestartPolicyManager>();
                    IRuntimeInfoProvider runtimeInfoProvider = await c.Resolve<Task<IRuntimeInfoProvider>>();
                    IEnvironmentProvider dockerEnvironmentProvider = await DockerEnvironmentProvider.CreateAsync(runtimeInfoProvider, moduleStateStore, restartPolicyManager);
                    return dockerEnvironmentProvider;
                })
             .As<Task<IEnvironmentProvider>>()
             .SingleInstance();
        }
    }
}

