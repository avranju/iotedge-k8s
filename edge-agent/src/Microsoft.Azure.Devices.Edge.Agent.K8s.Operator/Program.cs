// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices.Edge.Agent.K8s.Operator
{
    using System;
    using System.IO;
    using Newtonsoft.Json;
    using k8s;
    using static System.Environment;

    class Program
    {
        static void Main(string[] args)
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
            string path = $"apis/edge.azure-devices.net/v1beta1/watch/edgedeployments/edgy1";
            client.WatchObjectAsync<EdgeDeployment>(
                path,
                onEvent: (eventType, deployment) =>
                {
                    Console.WriteLine($"Event Type: {eventType}");
                    Console.WriteLine(JsonConvert.SerializeObject(deployment, Formatting.Indented));
                },
                onError: ex => Console.Error.WriteLine($"ERROR: {ex}"));

            Console.WriteLine("Press return key to quit.");
            Console.ReadLine();
        }
    }

    class EdgeModule
    {
        [JsonProperty(PropertyName = "id")]
        public String Id { get; set; }

        [JsonProperty(PropertyName = "image")]
        public String Image { get; set; }
    }

    class EdgeDeployment
    {
        [JsonProperty(PropertyName = "modules")]
        public EdgeModule[] Modules { get; set; }
    }
}
