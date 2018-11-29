// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Agent.Kubernetes.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using k8s;
    using k8s.Models;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Agent.Core.Serde;
    using Microsoft.Azure.Devices.Edge.Agent.Docker;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure_Devices.Edge.Agent.Kubernetes;
    using Microsoft.Rest;
    using Constants = Microsoft.Azure.Devices.Edge.Agent.Kubernetes.Constants;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;

    public class KubernetesCrdCommand<T> : ICommand
    {
        readonly IKubernetes client;
        readonly KubernetesModule[] modules;
        readonly Option<IRuntimeInfo> runtimeInfo;
        readonly Lazy<string> id;
        readonly ICombinedConfigProvider<T> combinedConfigProvider;
        readonly string iotHubHostname;
        readonly string deviceId;
        readonly TypeSpecificSerDe<EdgeDeploymentDefinition> deploymentSerde;
        readonly JsonSerializerSettings jsonSettings;

        public KubernetesCrdCommand(string iotHubHostname, string deviceId, IKubernetes client, KubernetesModule[] modules, Option<IRuntimeInfo> runtimeInfo, ICombinedConfigProvider<T> combinedConfigProvider)
        {
            this.iotHubHostname = Preconditions.CheckNonWhiteSpace(iotHubHostname, nameof(iotHubHostname));
            this.deviceId = Preconditions.CheckNonWhiteSpace(deviceId, nameof(deviceId));
            this.client = Preconditions.CheckNotNull(client, nameof(client));
            this.modules = Preconditions.CheckNotNull(modules, nameof(modules));
            this.runtimeInfo = Preconditions.CheckNotNull(runtimeInfo, nameof(runtimeInfo));
            this.combinedConfigProvider = Preconditions.CheckNotNull(combinedConfigProvider, nameof(combinedConfigProvider));
            this.id = new Lazy<string>(() => this.modules.Aggregate("", (prev, module) => module.ModuleIdentity.ModuleId + prev));
            var deserializerTypesMap = new Dictionary<Type, IDictionary<string, Type>>
            {
                [typeof(IModule)] = new Dictionary<string, Type>
                {
                    ["docker"] = typeof(CombinedDockerModule)
                },
            };

            this.deploymentSerde = new TypeSpecificSerDe<EdgeDeploymentDefinition>(deserializerTypesMap, new CamelCasePropertyNamesContractResolver());
            this.jsonSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };
        }

        // We use the sum of the IDs of the underlying commands as the id for this group
        // command.
        public string Id => this.id.Value;

        public async Task ExecuteAsync(CancellationToken token)
        {
            string resourceName = this.iotHubHostname + Constants.k8sNameDivider + this.deviceId;
            string metaApiVersion = Constants.k8sApi + "/" + Constants.k8sApiVersion;

            var modules = new List<KubernetesModule>();
            var secrets = new Dictionary<string, KubernetesSecret>();
            runtimeInfo.ForEach( runtime => {
                foreach (var m in this.modules)
                {
                    var combinedConfig = this.combinedConfigProvider.GetCombinedConfig(m.Module, runtime);
                    var combinedModule = new CombinedDockerModule(m.Module.Name, m.Module.Version, m.Module.DesiredStatus, m.Module.RestartPolicy, combinedConfig as CombinedDockerConfig, m.Module.ConfigurationInfo, m.Module.Env);
                    var combinedIdentity = new KubernetesModule(combinedModule, m.ModuleIdentity);
                    combinedModule.Config.AuthConfig.ForEach(
                        auth =>
                        {
                            var kubernetesAuth = new KubernetesSecret(auth);
                            secrets[kubernetesAuth.Name] = kubernetesAuth;
                        });
                    modules.Add(combinedIdentity);
                }
            });

            //TODO: Validate Spec here?

            Option<EdgeDeploymentDefinition> activeDeployment;
            try
            {
                HttpOperationResponse<object> currentDeployment = await this.client.GetNamespacedCustomObjectWithHttpMessagesAsync(Constants.k8sCrdGroup, Constants.k8sApiVersion, Constants.k8sNamespace, Constants.k8sCrdPlural, resourceName, cancellationToken: token);
                string body = JsonConvert.SerializeObject(currentDeployment.Body);
                Console.WriteLine("=================================================");
                Console.WriteLine(body);
                Console.WriteLine("=================================================");
                activeDeployment = currentDeployment.Response.IsSuccessStatusCode?
                    Option.Some(this.deploymentSerde.Deserialize(body)) :
                    Option.None<EdgeDeploymentDefinition>();
            }
            catch (Exception parseException)
            {
                Console.WriteLine(parseException.Message);
                activeDeployment = Option.None<EdgeDeploymentDefinition>();
            }

            foreach (KeyValuePair<string, KubernetesSecret> kubernetesSecret in secrets)
            {
                var secretData = new Dictionary<string, byte[]>();
                secretData[".dockerconfigjson"] = Encoding.UTF8.GetBytes(kubernetesSecret.Value.GenerateSecret());
                var secretMeta = new V1ObjectMeta(name: kubernetesSecret.Key);
                var newSecret = new V1Secret("v1", secretData, type: "kubernetes.io/dockerconfigjson",kind:"Secret", metadata: secretMeta);
                Option<V1Secret> currentSecret;
                try
                {
                    currentSecret = Option.Maybe(await this.client.ReadNamespacedSecretAsync(kubernetesSecret.Key, "default", cancellationToken: token));
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    currentSecret = Option.None<V1Secret>();
                }

                try
                {
                    await currentSecret.Match(
                        async s =>
                        {
                            if (!s.Data[".dockerconfigjson"].SequenceEqual(secretData[".dockerconfigjson"]))
                            {
                                return await this.client.ReplaceNamespacedSecretAsync(newSecret, kubernetesSecret.Key, "default", cancellationToken: token);
                            }

                            return s;
                        },
                        async () => await this.client.CreateNamespacedSecretAsync(newSecret, "default", cancellationToken: token));
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    Console.WriteLine(ex.Message);
                }
            }

            var metadata = new V1ObjectMeta(name: resourceName, namespaceProperty: Constants.k8sNamespace);
            // need resourceVersion for Replace.
            activeDeployment.ForEach( deployment => metadata.ResourceVersion = deployment.Metadata.ResourceVersion);
            var customObjectDefinition = new EdgeDeploymentDefinition(metaApiVersion, Constants.k8sCrdKind, metadata, modules);
            Console.WriteLine("=================================================");
            Console.WriteLine(this.deploymentSerde.Serialize(customObjectDefinition));
            Console.WriteLine("=================================================");
            // the dotnet client is apparently really picky about all names being camelCase
            object crdObject = JsonConvert.DeserializeObject(this.deploymentSerde.Serialize(customObjectDefinition));
            //object crdObject = customObjectDefinition;

            if (! activeDeployment.HasValue)
            {
                object response = await this.client.CreateNamespacedCustomObjectWithHttpMessagesAsync(
                    crdObject,
                    Constants.k8sCrdGroup,
                    Constants.k8sApiVersion,
                    Constants.k8sNamespace,
                    Constants.k8sCrdPlural,
                    cancellationToken: token);

            }
            else
            {
                object response = await this.client.ReplaceNamespacedCustomObjectWithHttpMessagesAsync(
                    crdObject,
                    Constants.k8sCrdGroup,
                    Constants.k8sApiVersion,
                    Constants.k8sNamespace,
                    Constants.k8sCrdPlural,
                    resourceName,
                    cancellationToken: token);
            }
        }

        public Task UndoAsync(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public string Show()
        {
            IEnumerable<string> commandDescriptions = this.modules.Select(m => $"[{m.Module.Name}]");
            return $"Create a CRD with modules: (\n  {string.Join("\n  ", commandDescriptions)}\n)";
        }

        public override string ToString() => this.Show();
    }
}
