{{- define "sandbox-service.name" -}}
{{- .Release.Name -}}
{{- end -}}

{{- define "sandbox-service.tag" -}}
{{- .Values.image.tag | default .Chart.AppVersion -}}
{{- end -}}

{{/* Selector labels are immutable on a Deployment: keep them to the one label that never changes. */}}
{{- define "sandbox-service.selectorLabels" -}}
app.kubernetes.io/name: {{ include "sandbox-service.name" . }}
{{- end -}}

{{- define "sandbox-service.labels" -}}
{{ include "sandbox-service.selectorLabels" . }}
app.kubernetes.io/part-of: sandbox
app.kubernetes.io/version: {{ include "sandbox-service.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "sandbox-service.probe" -}}
httpGet:
  path: {{ .path }}
  port: http
periodSeconds: {{ .periodSeconds }}
failureThreshold: {{ .failureThreshold }}
{{- end -}}
