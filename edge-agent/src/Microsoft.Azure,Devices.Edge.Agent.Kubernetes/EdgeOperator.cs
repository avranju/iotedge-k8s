// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure_Devices.Edge.Agent.Kubernetes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using k8s;
    using k8s.Models;
    using Microsoft.Azure.Devices.Edge.Agent.Core;
    using Microsoft.Azure.Devices.Edge.Agent.Core.Serde;
    using Microsoft.Azure.Devices.Edge.Agent.Kubernetes;
    using Microsoft.Azure.Devices.Edge.Storage;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.Concurrency;
    using Microsoft.Rest;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using Microsoft.Extensions.Logging;
    using DockerModels =  global::Docker.DotNet.Models;
    using AgentDocker = Microsoft.Azure.Devices.Edge.Agent.Docker;
    using Constants = Microsoft.Azure.Devices.Edge.Agent.Kubernetes.Constants;
    using VolumeOptions = Microsoft.Azure.Devices.Edge.Util.Option<(System.Collections.Generic.List<k8s.Models.V1Volume>, System.Collections.Generic.List<k8s.Models.V1VolumeMount>)>;

    public class EdgeOperator : IKubernetesOperator, IRuntimeInfoProvider
    {
        readonly IKubernetes client;

        Option<Watcher<V1Pod>> podWatch;
        readonly Dictionary<string, ModuleRuntimeInfo> moduleRuntimeInfos;
        readonly AsyncLock moduleLock;
        Option<Watcher<object>> operatorWatch;
        readonly string iotHubHostname;
        readonly string deviceId;
        readonly string resourceName;
        readonly string deploymentSelector;
        readonly TypeSpecificSerDe<EdgeDeploymentDefinition> deploymentSerde;
        readonly JsonSerializerSettings crdSerializerSettings;

        public EdgeOperator(string iotHubHostname, string deviceId, IKubernetes client)
        {
            this.iotHubHostname = Preconditions.CheckNonWhiteSpace(iotHubHostname, nameof(iotHubHostname));
            this.deviceId = Preconditions.CheckNonWhiteSpace(deviceId, nameof(deviceId));
            this.client = Preconditions.CheckNotNull(client, nameof(client));
            this.moduleRuntimeInfos = new Dictionary<string, ModuleRuntimeInfo>();
            this.moduleLock = new AsyncLock();
            this.podWatch = Option.None<Watcher<V1Pod>>();
            this.resourceName = this.iotHubHostname + Constants.k8sNameDivider + this.deviceId;
            this.deploymentSelector = Constants.k8sEdgeDeviceLabel + " = " + this.deviceId + "," + Constants.k8sEdgeHubNameLabel + "=" + this.iotHubHostname;
            var deserializerTypesMap = new Dictionary<Type, IDictionary<string, Type>>
            {
                [typeof(IModule)] = new Dictionary<string, Type>
                {
                    ["docker"] = typeof(CombinedDockerModule)
                }
            };

            this.deploymentSerde = new TypeSpecificSerDe<EdgeDeploymentDefinition>(deserializerTypesMap, new CamelCasePropertyNamesContractResolver());
            this.crdSerializerSettings = new JsonSerializerSettings
            {

                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };
        }

        public Task CloseAsync(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            this.podWatch.ForEach(watch => watch.Dispose());
            this.operatorWatch.ForEach(watch => watch.Dispose());
        }

        public async Task<IEnumerable<ModuleRuntimeInfo>> GetModules(CancellationToken ctsToken)
        {
            using (await this.moduleLock.LockAsync())
            {
                return this.moduleRuntimeInfos.Select(kvp => kvp.Value);
            }
        }

        public async Task<SystemInfo> GetSystemInfo()
        {
            V1NodeList k8sNodes = await this.client.ListNodeAsync();
            string osType = string.Empty;
            string arch = string.Empty;
            string version = string.Empty;
            var firstNode = k8sNodes.Items.FirstOrDefault();
            if (firstNode != null)
            {
                osType = firstNode.Status.NodeInfo.OperatingSystem;
                arch = firstNode.Status.NodeInfo.Architecture;
                version = firstNode.Status.NodeInfo.OsImage;
            }
            return new SystemInfo(osType, arch, version);
        }

        public void Start()
        {
            // The following "List..." requests do not return until there is something to return, so if we "await" here,
            // there is a chance that one or both of these requests will block forever - we won't start creating these pods and CRDs
            // until we receive a deployment.
            // Considering setting up these watches is critical to the operation of EdgeAgent, throwing an exception and letting the process crash
            // is an acceptable fate if these tasks fail.

            // Pod watching for module runtime status.
            this.client.ListNamespacedPodWithHttpMessagesAsync(Constants.k8sNamespace, watch: true).ContinueWith( async podListRespTask =>
            {
                if (podListRespTask != null)
                {
                    HttpOperationResponse<V1PodList> podListResp = await podListRespTask;
                    if (podListResp != null)
                    {
                        this.podWatch = Option.Some(podListResp.Watch<V1Pod>(async (type, item) =>
                        {
                            try
                            {
                                await this.WatchPodEventsAsync(type, item);
                            }
                            catch (Exception ex) when (!ex.IsFatal())
                            {
                                Events.ExceptionInPodWatch(ex);
                            }

                        }));
                    }
                    else
                    {
                        Events.NullListResponse("ListNamespacedPodWithHttpMessagesAsync", "http response");
                        throw new NullReferenceException("Null response from ListNamespacedPodWithHttpMessagesAsync");
                    }
                }
                else
                {
                    Events.NullListResponse("ListNamespacedPodWithHttpMessagesAsync", "task");
                    throw new NullReferenceException("Null Task from ListNamespacedPodWithHttpMessagesAsync");
                }
            });


            this.client.ListClusterCustomObjectWithHttpMessagesAsync(Constants.k8sCrdGroup, Constants.k8sApiVersion, Constants.k8sCrdPlural, watch: true).ContinueWith(
                async customObjectWatchTask =>
                {
                    if (customObjectWatchTask != null)
                    {
                        HttpOperationResponse<object> customObjectWatch = await customObjectWatchTask;
                        if (customObjectWatch != null)
                        {
                            // We can add events to a watch once created, like if connection is closed, etc.
                            this.operatorWatch = Option.Some(customObjectWatch.Watch<object>(
                                async (type, item) =>
                                {
                                    try
                                    {
                                        await this.WatchDeploymentEventsAsync(type, item);
                                    }
                                    catch (Exception ex) when (!ex.IsFatal())
                                    {
                                        Events.ExceptionInCustomResourceWatch(ex);
                                    }
                                }));
                        }
                        else
                        {
                            Events.NullListResponse("ListClusterCustomObjectWithHttpMessagesAsync", "http response");
                            throw new NullReferenceException("Null response from ListClusterCustomObjectWithHttpMessagesAsync");
                        }
                    }
                    else
                    {
                        Events.NullListResponse("ListClusterCustomObjectWithHttpMessagesAsync", "task");
                        throw new NullReferenceException("Null Task from ListClusterCustomObjectWithHttpMessagesAsync");
                    }
                });
        }

        Option<List<(int, string)>> GetExposedPorts(IDictionary<string, DockerModels.EmptyStruct> exposedPorts)
        {
            var serviceList = new List<(int, string)>();
            foreach (var exposedPort in exposedPorts)
            {
                string[] portProtocol = exposedPort.Key.Split('/');
                if (portProtocol.Length == 2)
                {
                    int port;
                    string protocol;
                    if (int.TryParse(portProtocol[0], out port) && this.ValidateProtocol(portProtocol[1], out protocol))
                    {
                        serviceList.Add((port, protocol));
                    }
                    else
                    {
                        Events.ExposedPortValue(exposedPort.Key);
                    }
                }
            }
            return (serviceList.Count > 0) ? Option.Some(serviceList) : Option.None<List<(int, string)>>();
        }

        Option<V1Service> GetServiceFromModule(Dictionary<string, string> labels, KubernetesModule module)
        {
            var serviceList = new List<V1ServicePort>();
            bool onlyExposedPorts = true;
            if (module.Module is IModule<AgentDocker.CombinedDockerConfig> moduleWithDockerConfig)
            {
                // Handle ExposedPorts entries
                if (moduleWithDockerConfig.Config?.CreateOptions?.ExposedPorts != null)
                {
                    this.GetExposedPorts(moduleWithDockerConfig.Config.CreateOptions.ExposedPorts)
                        .ForEach(exposedList =>
                            exposedList.ForEach((item) => serviceList.Add(new V1ServicePort(item.Item1, name: $"{item.Item1}-{item.Item2.ToLower()}", protocol: item.Item2))));
                }

                // Handle HostConfig PortBindings entries
                if (moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.PortBindings != null)
                {
                    foreach (var portBinding in moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.PortBindings)
                    {
                        string[] portProtocol = portBinding.Key.Split('/');
                        if (portProtocol.Length == 2)
                        {
                            int port;
                            string protocol;
                            if (int.TryParse(portProtocol[0], out port) && this.ValidateProtocol(portProtocol[1], out protocol))
                            {
                                // k8s requires HostPort to be above 30000, and I am ignoring HostIP
                                foreach (var hostBinding in portBinding.Value)
                                {
                                    int hostPort;
                                    if (int.TryParse(hostBinding.HostPort, out hostPort))
                                    {
                                        serviceList.Add(new V1ServicePort(port, name: $"{port}-{protocol.ToLower()}", protocol: protocol, targetPort: hostPort));
                                        onlyExposedPorts = false;
                                    }
                                    else
                                    {
                                        Events.PortBindingValue(module,portBinding.Key);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (serviceList.Count > 0)
            {
                // Selector: by module name and device name, also how we will label this puppy.
                var objectMeta = new V1ObjectMeta(labels: labels, name: module.ModuleIdentity.ModuleId.ToLower());
                // How we manage this service is dependent on the port mappings user asks for.
                string serviceType;
                if (onlyExposedPorts)
                {
                    serviceType = "ClusterIP";
                }
                else
                {
                    serviceType = "NodePort";
                }
                return Option.Some(new V1Service(metadata: objectMeta, spec: new V1ServiceSpec(type: serviceType, ports: serviceList, selector: labels)));
            }
            else
            {
                return Option.None<V1Service>();
            }
        }

        bool ValidateProtocol(string dockerProtocol, out string k8sProtocol)
        {
            bool result = true;
            switch (dockerProtocol.ToUpper())
            {
                case "TCP":
                    k8sProtocol = "TCP";
                    break;
                case "UDP":
                    k8sProtocol = "UDP";
                    break;
                case "SCTP":
                    k8sProtocol = "SCTP";
                    break;
                default:
                    k8sProtocol = "TCP"; 
                    result = false;
                    break;
            }
            return result;
        }

        async Task WatchDeploymentEventsAsync(WatchEventType type, object custom)
        {
            EdgeDeploymentDefinition customObject;
            try
            {
                string customString = JsonConvert.SerializeObject(custom);
                customObject = this.deploymentSerde.Deserialize(customString);
            }
            catch (Exception e)
            {
                Events.EdgeDeploymentDeserializeFail(e);
                return;
            }

            // only operate on the device that matches this operator.
            if (String.Equals(customObject.Metadata.Name, this.resourceName, StringComparison.OrdinalIgnoreCase))
            {
                V1ServiceList currentServices = await this.client.ListNamespacedServiceAsync(Constants.k8sNamespace, labelSelector: this.deploymentSelector);
                V1DeploymentList currentDeployments = await this.client.ListNamespacedDeploymentAsync(Constants.k8sNamespace, labelSelector: this.deploymentSelector);
                Events.DeploymentStatus(type,this.resourceName);
                switch (type)
                {
                    case WatchEventType.Added:
                    case WatchEventType.Modified:
                        this.ManageDeployments(currentServices, currentDeployments, customObject);
                        break;

                    case WatchEventType.Deleted:
                    {
                        // Delete the deployment.
                        // Delete any services.
                        var removeServiceTasks = currentServices.Items.Select(i => this.client.DeleteNamespacedServiceAsync(new V1DeleteOptions(), i.Metadata.Name, Constants.k8sNamespace));
                        await Task.WhenAll(removeServiceTasks);
                        var removeDeploymentTasks = currentDeployments.Items.Select(d => this.client.DeleteNamespacedDeployment1Async(new V1DeleteOptions(), d.Metadata.Name, Constants.k8sNamespace));
                        await Task.WhenAll(removeDeploymentTasks);
                    }
                        break;
                    case WatchEventType.Error:
                        Events.DeploymentError();
                        break;
                }
            }
            else
            {
                Events.DeploymentNameMismatch(customObject.Metadata.Name, this.resourceName);
            }

        }

        List<V1Service> GetCurrentServiceConfig(V1ServiceList currentServices)
        {
            return currentServices.Items.Select(
                service =>
                {

                    try
                    {
                        string creationString = service.Metadata.Annotations[Constants.CreationString];
                        V1Service createdService = JsonConvert.DeserializeObject<V1Service>(creationString);
                        return createdService;
                    }
                    catch (Exception ex)
                    {
                        Events.InvalidCreationString(ex);
                    }
                    return service;
                }).ToList();
        }

        List<V1Deployment> GetCurrentDeploymentConfig(V1DeploymentList currentDeployments)
        {
            return currentDeployments.Items.Select(
                deployment =>
                {
                    try
                    {
                        string creationString = deployment.Metadata.Annotations[Constants.CreationString];
                        V1Deployment createdDeployment = JsonConvert.DeserializeObject<V1Deployment>(creationString);
                        return createdDeployment;
                    }
                    catch (Exception ex)
                    {
                        Events.InvalidCreationString(ex);
                    }

                    return deployment;
                }).ToList();
        }

        async void ManageDeployments(V1ServiceList currentServices, V1DeploymentList currentDeployments, EdgeDeploymentDefinition customObject)
        {
            // PUll current configuration from annotations.
            List<V1Service> currentV1Services = this.GetCurrentServiceConfig(currentServices);
            List<V1Deployment> currentV1Deployments = this.GetCurrentDeploymentConfig(currentDeployments);

            var desiredServices = new List<V1Service>();
            var desiredDeployments = new List<V1Deployment>();
            foreach (var module in customObject.Spec)
            {
                if (string.Equals(module.Module.Type, "docker"))
                {
                    // Default labels
                    var labels = new Dictionary<string, string>();
                    labels.Add(Constants.k8sEdgeModuleLabel, module.ModuleIdentity.ModuleId);
                    labels.Add(Constants.k8sEdgeDeviceLabel, this.deviceId);
                    labels.Add(Constants.k8sEdgeHubNameLabel, this.iotHubHostname);

                    // Create a Service for every network interface of each module. (label them with hub, device and module id)
                    Option<V1Service> moduleService = this.GetServiceFromModule(labels, module);
                    moduleService.ForEach(service => desiredServices.Add(service));


                    // Create a Pod for each module, and a proxy container.
                    V1PodTemplateSpec v1PodSpec = this.GetPodFromModule(labels, module);
                    // Bundle into a deployment
                    string deploymentName = this.iotHubHostname + "-" + this.deviceId.ToLower() + "-"
                        + module.ModuleIdentity.ModuleId.ToLower() + "-deployment";
                    // Deployment data
                    var deploymentMeta = new V1ObjectMeta(name: deploymentName, labels: labels);

                    var selector = new V1LabelSelector(matchLabels: labels);
                    var deploymentSpec = new V1DeploymentSpec(replicas: 1, selector: selector, template: v1PodSpec);
                    desiredDeployments.Add(new V1Deployment(metadata: deploymentMeta, spec: deploymentSpec));

                    // Make the client call for the deployment
                    // V1Deployment deploymentResult = await this.client.CreateNamespacedDeploymentAsync(deployment, Constants.k8sNamespace);
                    // What does the result tell us?
                }
                else
                {
                    Events.InvalidModuleType(module);
                }
            }

            // Find current Services/Deployments which need to be removed and updated
            var servicesRemoved = new List<V1Service>(currentServices.Items);
            servicesRemoved.RemoveAll(s => desiredServices.Exists(i => string.Equals(i.Metadata.Name, s.Metadata.Name)));
            var deploymentsRemoved = new List<V1Deployment>(currentDeployments.Items);
            deploymentsRemoved.RemoveAll(d => desiredDeployments.Exists(i => string.Equals(i.Metadata.Name, d.Metadata.Name)));

            var newServices = new List<V1Service>();
            var currentServicesList = currentServices.Items.ToList();
            desiredServices.ForEach(
                s =>
                {
                    if (currentServicesList.Exists(i => string.Equals(i.Metadata.Name, s.Metadata.Name)))
                    {
                        V1Service currentCreated = currentV1Services.Find(i => string.Equals(i.Metadata.Name, s.Metadata.Name));
                        if (V1ServiceEx.ServiceEquals(currentCreated, s))
                            return;
                        string creationString = JsonConvert.SerializeObject(s);
                        if (s.Metadata.Annotations == null)
                        {
                            var annotations = new Dictionary<string, string>();
                            annotations.Add(Constants.CreationString, creationString);
                            s.Metadata.Annotations = annotations;
                        }
                        else
                        {
                            s.Metadata.Annotations[Constants.CreationString] = creationString;
                        }

                        servicesRemoved.Add(s);
                        newServices.Add(s);
                        Events.UpdateService(s.Metadata.Name);
                    }
                    else
                    {
                        string creationString = JsonConvert.SerializeObject(s);
                        var annotations = new Dictionary<string, string>();
                        annotations.Add(Constants.CreationString, creationString);
                        s.Metadata.Annotations = annotations;
                        newServices.Add(s);
                        Events.CreateService(s.Metadata.Name);
                    }
                });
            var deploymentsUpdated = new List<V1Deployment>();
            var newDeployments = new List<V1Deployment>();
            var currentDeploymentsList = currentDeployments.Items.ToList();
            desiredDeployments.ForEach(
                d =>
                {
                    if (currentDeploymentsList.Exists(i => string.Equals(i.Metadata.Name, d.Metadata.Name)))
                    {
                        V1Deployment current = currentDeploymentsList.Find(i => string.Equals(i.Metadata.Name, d.Metadata.Name));
                        V1Deployment currentCreated = currentV1Deployments.Find(i => string.Equals(i.Metadata.Name, d.Metadata.Name));
                        if (V1DeploymentEx.DeploymentEquals(currentCreated, d))
                            return;
                        string creationString = JsonConvert.SerializeObject(d);
                        d.Metadata.ResourceVersion = current.Metadata.ResourceVersion;
                        if (d.Metadata.Annotations == null)
                        {
                            var annotations = new Dictionary<string, string>();
                            annotations.Add(Constants.CreationString, creationString);
                            d.Metadata.Annotations = annotations;
                        }
                        else
                        {
                            d.Metadata.Annotations[Constants.CreationString] = creationString;
                        }
                        deploymentsUpdated.Add(d);
                        Events.UpdateDeployment(d.Metadata.Name);
                    }
                    else
                    {
                        string creationString = JsonConvert.SerializeObject(d);
                        var annotations = new Dictionary<string, string>();
                        annotations.Add(Constants.CreationString, creationString);
                        d.Metadata.Annotations = annotations;
                        newDeployments.Add(d);
                        Events.CreateDeployment(d.Metadata.Name);
                    }
                });

            // Remove the old
            var removeServiceTasks = servicesRemoved.Select(i => this.client.DeleteNamespacedServiceAsync(new V1DeleteOptions(), i.Metadata.Name, Constants.k8sNamespace));
            await Task.WhenAll(removeServiceTasks);
            var removeDeploymentTasks = deploymentsRemoved.Select(d => this.client.DeleteNamespacedDeployment1Async(new V1DeleteOptions(), d.Metadata.Name, Constants.k8sNamespace));
            await Task.WhenAll(removeDeploymentTasks);

            // Create the new.
            var createServiceTasks = newServices.Select(s => this.client.CreateNamespacedServiceAsync(s, Constants.k8sNamespace));
            await Task.WhenAll(createServiceTasks);
            var createDeploymentTasks = newDeployments.Select(deployment => this.client.CreateNamespacedDeploymentAsync(deployment, Constants.k8sNamespace));
            await Task.WhenAll(createDeploymentTasks);

            // Update the existing - should only do this when different.
            //var updateServiceTasks = servicesUpdated.Select( s => this.client.ReplaceNamespacedServiceAsync(s, s.Metadata.Name, Constants.k8sNamespace));
            //await Task.WhenAll(updateServiceTasks);
            var updateDeploymentTasks = deploymentsUpdated.Select(deployment => this.client.ReplaceNamespacedDeploymentAsync(deployment, deployment.Metadata.Name, Constants.k8sNamespace));
            await Task.WhenAll(updateDeploymentTasks);
        }

        V1PodTemplateSpec GetPodFromModule(Dictionary<string, string> labels, KubernetesModule module)
        {
            if (module.Module is IModule<AgentDocker.CombinedDockerConfig> moduleWithDockerConfig)
            {
                //pod labels
                var podLabels = new Dictionary<string, string>(labels);
                if (moduleWithDockerConfig.Config.CreateOptions?.Labels != null)
                {
                    foreach (var label in moduleWithDockerConfig.Config.CreateOptions?.Labels)
                    {
                        podLabels.Add(label.Key, label.Value);
                    }
                }


                // Per container settings:
                // exposed ports
                Option<List<V1ContainerPort>> exposedPortsOption = (moduleWithDockerConfig.Config?.CreateOptions?.ExposedPorts != null) ?
                    this.GetExposedPorts(moduleWithDockerConfig.Config.CreateOptions.ExposedPorts).Map(servicePorts =>
                       servicePorts.Select(tuple => new V1ContainerPort(tuple.Item1, protocol: tuple.Item2)).ToList()) :
                    Option.None<List<V1ContainerPort>>();

                // privileged container
                Option<V1SecurityContext> securityContext = (moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.Privileged == true) ?
                    Option.Some(new V1SecurityContext(privileged: true)) :
                    Option.None<V1SecurityContext>();

                // Environment Variables.
                List<V1EnvVar> env = this.CollectEnv(moduleWithDockerConfig);

                // Bind mounts
                (List<V1Volume> volumeList, List<V1VolumeMount> volumeMountList) = this.GetVolumesFromModule(moduleWithDockerConfig).GetOrElse((null, null));

                //Image
                string moduleImage = moduleWithDockerConfig.Config.Image;

                var moduleContainer = new V1Container(module.ModuleIdentity.ModuleId.ToLower(),
                                                              env: env,
                                                              image: moduleImage,
                                                              volumeMounts: volumeMountList,
                                                              securityContext: securityContext.GetOrElse(() => null),
                                                              ports:exposedPortsOption.GetOrElse(() => null)
                                                              );
                var containerList = new List<V1Container>();
                containerList.Add(moduleContainer);
                // TODO: Add Proxy container here

                //
                Option<List<V1LocalObjectReference>> imageSecret = moduleWithDockerConfig.Config.AuthConfig.Map(
                    auth =>
                    {
                        var secret = new KubernetesSecret(auth);
                        var auth1 = new V1LocalObjectReference(secret.Name);
                        var authList = new List<V1LocalObjectReference>();
                        authList.Add(auth1);
                        return authList;
                    });
                var modulePodSpec = new V1PodSpec(containerList, volumes: volumeList, imagePullSecrets: imageSecret.GetOrElse(() => null));

                var objectMeta = new V1ObjectMeta(labels: podLabels);
                return new V1PodTemplateSpec(metadata: objectMeta, spec: modulePodSpec);
            }
            else
            {
                Events.InvalidModuleType(module);
            }
            return new V1PodTemplateSpec();
        }

        VolumeOptions GetVolumesFromModule(IModule<AgentDocker.CombinedDockerConfig> moduleWithDockerConfig)
        {
            if ((moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.Binds == null) && (moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.Mounts == null))
                return Option.None<(List<V1Volume>, List<V1VolumeMount>)>();

            var volumeList = new List<V1Volume>();
            var volumeMountList = new List<V1VolumeMount>();
            if (moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.Binds != null)
            {
                foreach (var bind in moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.Binds)
                {
                    string[] bindSubstrings = bind.Split(':');
                    if (bindSubstrings.Count() >= 2)
                    {
                        string name = bindSubstrings[0];
                        string type = "DirectoryOrCreate";
                        string hostPath = bindSubstrings[0];
                        string mountPath = bindSubstrings[1];
                        bool readOnly = ((bindSubstrings.Count() > 2) && bindSubstrings[2].Contains("ro"));
                        volumeList.Add(new V1Volume(name, hostPath: new V1HostPathVolumeSource(hostPath, type)));
                        volumeMountList.Add(new V1VolumeMount(mountPath, name, readOnlyProperty: readOnly));
                    }
                }
            }

            if (moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.Mounts != null)
            {
                foreach (var mount in moduleWithDockerConfig.Config?.CreateOptions?.HostConfig?.Mounts)
                {
                    if (mount.Type.Equals("bind", StringComparison.InvariantCultureIgnoreCase))
                    {
                        string name = mount.Source;
                        string type = "DirectoryOrCreate";
                        string hostPath = mount.Source;
                        string mountPath = mount.Target;
                        bool readOnly = mount.ReadOnly;
                        volumeList.Add(new V1Volume(name, hostPath: new V1HostPathVolumeSource(hostPath, type)));
                        volumeMountList.Add(new V1VolumeMount(mountPath, name, readOnlyProperty: readOnly));
                    }
                }
            }

            if (volumeList.Count > 0  || volumeMountList.Count > 0)
            {
                return Option.Some((volumeList, volumeMountList));
            }

            return Option.None<(List<V1Volume>,List<V1VolumeMount>)>();
        }

        List<V1EnvVar> CollectEnv(IModule<AgentDocker.CombinedDockerConfig> moduleWithDockerConfig)
        {
            char[] envSplit = { '=' };
            var envList = new List<V1EnvVar>();
            foreach (var item in moduleWithDockerConfig.Env)
            {
                envList.Add(new V1EnvVar(item.Key, item.Value.Value));
            }

            if (moduleWithDockerConfig.Config?.CreateOptions?.Env != null)
            {
                foreach (var hostEnv in moduleWithDockerConfig.Config?.CreateOptions?.Env)
                {
                    string[] key_value = hostEnv.Split(envSplit, 2);
                    if (key_value.Count() == 2)
                    {
                        envList.Add(new V1EnvVar(key_value[0], key_value[1]));
                    }
                }
            }

            return envList;
        }

        async Task WatchPodEventsAsync(WatchEventType type, V1Pod item)
        {
            string podName = item.Metadata.Labels[Constants.k8sEdgeModuleLabel];
            Events.PodStatus(type,podName);
            switch (type)
            {
                case WatchEventType.Added:
                case WatchEventType.Modified:
                case WatchEventType.Error:
                    var runtimeInfo = this.ConvertPodToRuntime(podName, item);
                    using (await this.moduleLock.LockAsync())
                    {
                        this.moduleRuntimeInfos[podName] = runtimeInfo;
                    }
                    break;
                case WatchEventType.Deleted:
                    Option<ModuleRuntimeInfo> removedModuleInfo = Option.None<ModuleRuntimeInfo>();
                    using (await this.moduleLock.LockAsync())
                    {
                        ModuleRuntimeInfo removedRuntimeInfo;
                        if (this.moduleRuntimeInfos.TryRemove(podName,out removedRuntimeInfo))
                        {
                            removedModuleInfo = Option.Some(removedRuntimeInfo);
                        }
                    }
                    break;

            }
        }

        (ModuleStatus, string) ConvertPodStatusToModuleStatus(Option<V1ContainerStatus> podStatus)
        {
            // TODO: Possibly refine this?
            return podStatus.Map(pod =>
                {
                    if (pod.State.Running != null)
                    {
                        return (ModuleStatus.Running, $"Started at {pod.State.Running.StartedAt.GetValueOrDefault(DateTime.Now)}");
                    }
                    else if (pod.State.Terminated != null)
                    {
                        return (ModuleStatus.Failed, pod.State.Terminated.Message);
                    }
                    else if (pod.State.Waiting != null)
                    {
                        return (ModuleStatus.Failed, pod.State.Waiting.Message);
                    }
                    return (ModuleStatus.Unknown, "Unknown");
                }).GetOrElse(() => (ModuleStatus.Unknown, "Unknown"));
        }


        Option<V1ContainerStatus> GetContainerByName(string name, V1Pod pod)
        {
            if (pod.Status?.ContainerStatuses != null)
            {
                foreach (var status in pod.Status.ContainerStatuses)
                {
                    if (string.Equals(status.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return Option.Some(status);
                    }
                }
            }

            return Option.None<V1ContainerStatus>();
        }

        (int, Option<DateTime>, Option<DateTime>, string image) GetRuntimedata(V1ContainerStatus status)
        {
            if (status.LastState?.Running != null)
            {
                if (status.LastState.Running.StartedAt.HasValue)
                {
                    return (0, Option.Some(status.LastState.Running.StartedAt.Value), Option.None<DateTime>(), status.Image);
                }
            }
            else if (status.LastState?.Terminated != null)
            {
                if (status.LastState.Terminated.StartedAt.HasValue &&
                    status.LastState.Terminated.FinishedAt.HasValue)
                {
                    return (0, Option.Some(status.LastState.Terminated.StartedAt.Value), Option.Some(status.LastState.Terminated.FinishedAt.Value), status.Image);
                }
            }
            return (0, Option.None<DateTime>(), Option.None<DateTime>(), String.Empty);
        }

        ModuleRuntimeInfo ConvertPodToRuntime(string name, V1Pod pod)
        {
            var containerStatus = this.GetContainerByName(name, pod);
            (var moduleStatus, var statusDescription) = this.ConvertPodStatusToModuleStatus(containerStatus);
            (var exitCode, var startTime, var exitTime, var imageHash) = this.GetRuntimedata(containerStatus.OrDefault());
            var reportedConfig = new AgentDocker.DockerReportedConfig(string.Empty, string.Empty, imageHash);
            return new ModuleRuntimeInfo<AgentDocker.DockerReportedConfig>(name, "docker", moduleStatus, statusDescription, exitCode, startTime, exitTime, reportedConfig);
            //return new ModuleRuntimeInfo(name, "docker", moduleStatus, pod.Status.Message, exitCode, startTime, exitTime);
        }

        static class Events
        {
            static readonly ILogger Log = Logger.Factory.CreateLogger<EdgeOperator>();
            const int IdStart = AgentEventIds.KubernetesOperator;

            enum EventIds
            {
                InvalidModuleType = IdStart,
                ExceptionInPodWatch,
                ExceptionInCustomResourceWatch,
                InvalidCreationString,
                ExposedPortValue,
                PortBindingValue,
                EdgeDeploymentDeserializeFail,
                DeploymentStatus,
                DeploymentError,
                DeploymentNameMismatch,
                PodStatus,
                RemoveService,
                UpdateService,
                CreateService,
                RemoveDeployment,
                UpdateDeployment,
                CreateDeployment,
                NullListResponse,
            }

            public static void InvalidModuleType(KubernetesModule module)
            {
                Log.LogError((int)EventIds.InvalidModuleType, $"Module {module.Module.Name} has an invalid module type '{module.Module.Type}'. Expected type 'docker'");
            }

            public static void ExceptionInPodWatch(Exception ex)
            {
                Log.LogError((int)EventIds.ExceptionInPodWatch, ex, "Exception caught in Pod Watch task.");
            }
            public static void ExceptionInCustomResourceWatch(Exception ex)
            {
                Log.LogError((int)EventIds.ExceptionInCustomResourceWatch, ex, "Exception caught in Custom Resource Watch task.");
            }
            public static void InvalidCreationString(Exception ex)
            {
                Log.LogWarning((int)EventIds.InvalidCreationString, ex, "Expected a valid creation string in k8s Object.");
            }

            public static void ExposedPortValue(string portEntry)
            {
                Log.LogWarning((int)EventIds.ExposedPortValue, $"Received an invalid exposed port value '{portEntry}'.");
            }

            public static void PortBindingValue(KubernetesModule module, string portEntry)
            {
                Log.LogWarning((int)EventIds.PortBindingValue, $"Module {module.Module.Name} has an invalid port binding value '{portEntry}'.");
            }
            public static void EdgeDeploymentDeserializeFail(Exception e)
            {
                Log.LogError((int)EventIds.EdgeDeploymentDeserializeFail, e, "Received an invalid Edge Deployment.");
            }
            public static void DeploymentStatus(WatchEventType type, string name)
            {
                Log.LogDebug((int)EventIds.DeploymentStatus, $"Pod '{name}', status'{type}'");
            }

            public static void DeploymentError()
            {
                Log.LogError((int)EventIds.DeploymentError, "Operator received error on watch type.");
            }

            public static void DeploymentNameMismatch(string received, string expected)
            {
                Log.LogDebug((int)EventIds.DeploymentNameMismatch, $"Watching for edge deployments for '{expected}', received notification for '{received}'");
            }

            public static void PodStatus(WatchEventType type, string podname)
            {
                Log.LogDebug((int)EventIds.PodStatus, $"Pod '{podname}', status'{type}'");
            }
            public static void RemoveService(string name)
            {
                Log.LogDebug((int)EventIds.RemoveService, $"Removing service '{name}'");
            }

            public static void UpdateService(string name)
            {
                Log.LogDebug((int)EventIds.UpdateService, $"Updating service '{name}'");
            }

            public static void CreateService(string name)
            {
                Log.LogDebug((int)EventIds.CreateService, $"Creating service '{name}'");
            }
            public static void RemoveDeployment(string name)
            {
                Log.LogDebug((int)EventIds.RemoveDeployment, $"Removing edge deployment '{name}'");
            }
            public static void UpdateDeployment(string name)
            {
                Log.LogDebug((int)EventIds.UpdateDeployment, $"Updating edge deployment '{name}'");
            }
            public static void CreateDeployment(string name)
            {
                Log.LogDebug((int)EventIds.CreateDeployment, $"Creating edge deployment '{name}'");
            }
            public static void NullListResponse(string listType, string what)
            {
                Log.LogError((int)EventIds.NullListResponse, $"{listType} returned null {what}");
            }
        }
    }
}
