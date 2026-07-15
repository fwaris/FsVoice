{{- define "fsvoice.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "fsvoice.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name (include "fsvoice.name" .) | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}

{{- define "fsvoice.labels" -}}
app.kubernetes.io/name: {{ include "fsvoice.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
{{- end }}

{{- define "fsvoice.assetsSecretName" -}}
{{- printf "%s-assets" (include "fsvoice.fullname" .) }}
{{- end }}
