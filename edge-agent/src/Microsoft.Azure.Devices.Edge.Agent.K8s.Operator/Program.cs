// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices.Edge.Agent.K8s.Operator
{
    using System;
    using System.IO;
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
        }
    }
}
