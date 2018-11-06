// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Agent.Core
{
    using Microsoft.Azure.Devices.Edge.Util;

    public class KubernetesModuleIdentity 
    {
        public KubernetesModuleIdentity(string iotHubHostname, string gatewayHostname, string deviceId, string moduleId)
        {
            this.IotHubHostname = Preconditions.CheckNonWhiteSpace(iotHubHostname, nameof(this.IotHubHostname));
            this.GatewayHostname = gatewayHostname;
            this.DeviceId = Preconditions.CheckNonWhiteSpace(deviceId, nameof(this.DeviceId));
            this.ModuleId = Preconditions.CheckNonWhiteSpace(moduleId, nameof(this.ModuleId));
        }

        public string IotHubHostname { get; }

        public string GatewayHostname { get; }

        public string DeviceId { get; }

        public string ModuleId { get; }
    }
}
