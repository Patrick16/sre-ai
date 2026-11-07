{{- define "postgres.selectorLabels" -}}
app.kubernetes.io/name: {{ .Release.Name }}
{{- end -}}

{{- define "postgres.labels" -}}
{{ include "postgres.selectorLabels" . }}
app.kubernetes.io/part-of: sandbox
app.kubernetes.io/component: database
app.kubernetes.io/version: {{ .Values.image.tag | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{/* pg_isready over TCP on localhost: checks that postgres accepts connections. */}}
{{- define "postgres.isready" -}}
exec:
  command: ["sh", "-c", "pg_isready -h 127.0.0.1 -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\""]
{{- end -}}
