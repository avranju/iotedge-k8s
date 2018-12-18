// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Agent.Service.Modules
{
    using System;
    using System.Collections.Generic;
    using static System.Environment;
    using System.IO;
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
        readonly string proxyImage;
        readonly string proxyConfigPath;
        readonly string proxyConfigVolumeName;
        readonly string serviceAccountName;
        readonly Uri managementUri;
        readonly Uri workloadUri;
        readonly IEnumerable<AuthConfig> dockerAuthConfig;
        readonly Option<UpstreamProtocol> upstreamProtocol;
        readonly Option<string> productInfo;

        public KubernetesModule(string iotHubHostname, string gatewayHostName, string deviceId, string proxyImage, string proxyConfigPath, string proxyConfigVolumeName,
            string serviceAccountName, Uri managementUri, Uri workloadUri, IEnumerable<AuthConfig> dockerAuthConfig, Option<UpstreamProtocol> upstreamProtocol, Option<string> productInfo)
        {

            this.iotHubHostname = Preconditions.CheckNonWhiteSpace(iotHubHostname, nameof(iotHubHostname));
            this.gatewayHostname = Preconditions.CheckNonWhiteSpace(gatewayHostName, nameof(gatewayHostName));
            this.deviceId = Preconditions.CheckNonWhiteSpace(deviceId, nameof(deviceId));
            this.proxyImage = Preconditions.CheckNonWhiteSpace(proxyImage, nameof(proxyImage));
            this.proxyConfigPath = Preconditions.CheckNonWhiteSpace(proxyConfigPath, nameof(proxyConfigPath));
            this.proxyConfigVolumeName = Preconditions.CheckNonWhiteSpace(proxyConfigVolumeName, nameof(proxyConfigVolumeName));
            this.serviceAccountName = Preconditions.CheckNonWhiteSpace(serviceAccountName, nameof(serviceAccountName));
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
                    // load the k8s config from $HOME/.kube/config if its available
                    KubernetesClientConfiguration kubeConfig;
                    string kubeConfigPath = Path.Combine(Environment.GetFolderPath(SpecialFolder.UserProfile), ".kube", "config");
                    if (File.Exists(kubeConfigPath))
                    {
                        kubeConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile();
                    }
                    else
                    {
                        kubeConfig = KubernetesClientConfiguration.InClusterConfig();
                    }

                    var client = new Kubernetes(kubeConfig);
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
                    c => Task.FromResult(new EdgeOperator(this.iotHubHostname, this.deviceId, this.gatewayHostname, this.proxyImage, this.proxyConfigPath,
                                         this.proxyConfigVolumeName, this.serviceAccountName, this.workloadUri, this.managementUri, c.Resolve<IKubernetes>()) as IRuntimeInfoProvider))
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

