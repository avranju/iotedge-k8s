{{/* vim: set filetype=mustache: */}}
{{/*
Expand the name of the chart.
*/}}
{{- define "edge-kubernetes.name" -}}
{{- default .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Create a default fully qualified app name.
We truncate at 63 chars because some Kubernetes name fields are limited to this (by the DNS naming spec).
If release name contains chart name it will be used as a full name.
*/}}
{{- define "edge-kubernetes.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "edge-kubernetes.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Template for iotedged's configuration YAML. */}}
{{- define "edge-kubernetes.iotedgedconfig" }}
provisioning:
  source: "manual"
  device_connection_string: {{ .Values.deviceConnectionString | quote }}
agent:
  name: "edgeAgent"
  type: "docker"
  env: {}
  config:
    image: "{{ .Values.edgeAgent.repository }}:{{ .Values.edgeAgent.tag }}"
    {{- if .Values.edgeAgent.registryCredentials }}
    auth:
      username: {{ .Values.edgeAgent.registryCredentials.username | quote }}
      password: {{ .Values.edgeAgent.registryCredentials.password | quote }}
      serveraddress: {{ .Values.edgeAgent.registryCredentials.serveraddress | quote }}
    {{ else }}
    auth: {}
    {{ end }}
hostname: "localhost"
connect:
  management_uri: "http://0.0.0.0:{{ .Values.iotedged.ports.management }}"
  workload_uri: "http://0.0.0.0:{{ .Values.iotedged.ports.workload }}"
listen:
  management_uri: "http://0.0.0.0:{{ .Values.iotedged.ports.management }}"
  workload_uri: "http://0.0.0.0:{{ .Values.iotedged.ports.workload }}"
homedir: "/var/lib/iotedge"
moby_runtime:
  uri: "unix:///var/run/docker.sock"
  network: "azure-iot-edge"
{{ end }}

{{/* Template for rendering registry credentials. */}}
{{- define "edge-kubernetes.regcreds" }}
auths:
  {{- range $key, $val := .Values.registryCredentials }}
  {{ $key | quote }}:
    auth: {{ printf "%s:%s" $val.username $val.password | b64enc | quote }}
  {{- end }}
{{- end }}
