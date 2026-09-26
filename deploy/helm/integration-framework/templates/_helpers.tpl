{{- define "integration-framework.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "integration-framework.labels" -}}
app.kubernetes.io/name: {{ include "integration-framework.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "integration-framework.selectorLabels" -}}
app.kubernetes.io/name: {{ include "integration-framework.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "integration-framework.metadataConnectionString" -}}
{{- if .Values.metadata.existingSecret -}}
{{- printf "%s" .Values.metadata.existingSecretKey -}}
{{- else -}}
{{- printf "%s" .Values.metadata.connectionStringBuilder -}}
{{- end -}}
{{- end -}}
