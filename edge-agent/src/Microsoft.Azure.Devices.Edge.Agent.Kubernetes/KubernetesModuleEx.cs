// Copyright (c) Microsoft. All rights reserved.


namespace Microsoft.Azure_Devices.Edge.Agent.Kubernetes
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using k8s.Models;
    using Microsoft.Azure.Devices.Edge.Util;

    public static class V1ObjectMetaEx
    {
        static readonly DictionaryComparer<string, string> StringDictionaryComparer = new DictionaryComparer<string, string>();

        public static bool ObjMetaEquals(V1ObjectMeta self, V1ObjectMeta other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(self, other))
            {
                return true;
            }

            return string.Equals(self.Name, other.Name) &&
                StringDictionaryComparer.Equals(self.Labels, other.Labels);
        }
    }

    public static class V1PodSpecEx
    {
        public static bool PodSpecEquals(V1PodSpec self, V1PodSpec other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(self, other))
            {
                return true;
            }
            // TODO: Containers, Volumes
            return true;
        }
    }

    public static class V1PodTemplateSpecEx
    {
        public static bool PodTemplateEquals(V1PodTemplateSpec self, V1PodTemplateSpec other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(self, other))
            {
                return true;
            }

            return V1ObjectMetaEx.ObjMetaEquals(self.Metadata,other.Metadata) &&
                V1PodSpecEx.PodSpecEquals(self.Spec,other.Spec);
        }
    }

    public static class V1DeploymentSpecEx
    {
        public static bool SpecEquals(V1DeploymentSpec self, V1DeploymentSpec other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(self, other))
            {
                return true;
            }

            return V1PodTemplateSpecEx.PodTemplateEquals(self.Template,other.Template);
        }
    }
    public static class V1DeploymentEx
    {
        public static bool DeploymentEquals(V1Deployment self, V1Deployment other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(self, other))
            {
                return true;
            }

            return string.Equals(self.ApiVersion, other.ApiVersion) &&
                string.Equals(self.Kind, other.Kind) &&
                V1ObjectMetaEx.ObjMetaEquals(self.Metadata,other.Metadata) &&
                V1DeploymentSpecEx.SpecEquals(self.Spec,other.Spec);

        }
    }

    public static class V1ServiceSpecEx
    {
        public static bool SpecEquals(V1ServiceSpec self, V1ServiceSpec other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(self, other))
            {
                return true;
            }

            if (self.Ports.Count != other.Ports.Count)
            {
                return false;
            }

            return string.Equals(self.Type, other.Type);
        }
    }
    public static class V1ServiceEx
    {
        public static bool ServiceEquals(V1Service self, V1Service other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(self, other))
            {
                return true;
            }

            return string.Equals(self.ApiVersion, other.ApiVersion) &&
                string.Equals(self.Kind, other.Kind) &&
                V1ObjectMetaEx.ObjMetaEquals(self.Metadata, other.Metadata) &&
                V1ServiceSpecEx.SpecEquals(self.Spec, other.Spec);
        }
    }
}
