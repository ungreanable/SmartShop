{{- define "smartshop.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "smartshop.labels" -}}
app.kubernetes.io/name: {{ .Chart.Name }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "smartshop.selector" -}}
app.kubernetes.io/name: {{ .root.Chart.Name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{- define "smartshop.image" -}}
{{- printf "%s/%s/smartshop-%s:%s" .root.Values.image.registry .root.Values.image.owner .name (default .root.Chart.AppVersion .root.Values.image.tag) -}}
{{- end -}}

{{- define "smartshop.secretName" -}}
{{- default (printf "%s-secrets" (include "smartshop.fullname" .)) .Values.existingSecret -}}
{{- end -}}

{{/* Environment shared by api, worker and the migration job. */}}
{{- define "smartshop.appEnv" -}}
- name: ASPNETCORE_ENVIRONMENT
  value: Production
- name: App__PublicUrl
  value: {{ .Values.publicUrl | quote }}
- name: Auth__Line__ChannelId
  value: {{ .Values.config.lineLoginChannelId | quote }}
- name: Auth__Line__LiffId
  value: {{ .Values.config.lineLiffId | quote }}
- name: Line__MonthlyQuota
  value: {{ .Values.config.lineMonthlyQuota | quote }}
- name: Line__AddFriendUrl
  value: {{ .Values.config.lineAddFriendUrl | quote }}
- name: Identity__SystemAdminLineUserIds__0
  value: {{ .Values.config.systemAdminLineUserId | quote }}
- name: WebPush__Subject
  value: {{ printf "mailto:%s" .Values.config.adminEmail | quote }}
- name: WebPush__PublicKey
  value: {{ .Values.config.vapidPublicKey | quote }}
- name: Storage__Provider
  value: {{ .Values.config.storage.provider | quote }}
- name: Storage__ServiceUrl
  value: {{ .Values.config.storage.serviceUrl | quote }}
- name: Storage__Bucket
  value: {{ .Values.config.storage.bucket | quote }}
- name: Storage__AccessKey
  value: {{ .Values.config.storage.accessKey | quote }}
- name: Payments__SlipVerifier__Url
  value: {{ .Values.config.slipVerifier.url | quote }}
- name: Payments__SlipVerifier__AutoConfirm
  value: {{ .Values.config.slipVerifier.autoConfirm | quote }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ .Values.config.otlpEndpoint | quote }}
- name: Database__RowLevelSecurity
  value: {{ .Values.config.rowLevelSecurity | quote }}
{{- range $key, $secretKey := dict "ConnectionStrings__smartshop" "postgresConnection" "ConnectionStrings__cache" "cacheConnection" "ConnectionStrings__rabbitmq" "rabbitmqConnection" "Auth__Jwt__SigningKey" "jwtSigningKey" "Media__SigningKey" "mediaSigningKey" "Auth__Line__ChannelSecret" "lineLoginChannelSecret" "Line__MessagingChannelAccessToken" "lineMessagingAccessToken" "Line__MessagingChannelSecret" "lineMessagingChannelSecret" "WebPush__PrivateKey" "vapidPrivateKey" "Storage__SecretKey" "storageSecretKey" "Payments__SlipVerifier__ApiKey" "slipVerifierApiKey" "Firebase__CredentialsJson" "firebaseCredentialsJson" }}
- name: {{ $key }}
  valueFrom:
    secretKeyRef:
      name: {{ include "smartshop.secretName" $ }}
      key: {{ $secretKey }}
      optional: true
{{- end }}
{{- range $key, $value := .Values.config.extraEnv }}
- name: {{ $key }}
  value: {{ $value | quote }}
{{- end }}
{{- end -}}

{{- define "smartshop.probes" -}}
startupProbe:
  httpGet: { path: /health/live, port: http }
  periodSeconds: 5
  failureThreshold: 30
livenessProbe:
  httpGet: { path: /health/live, port: http }
  periodSeconds: 20
readinessProbe:
  httpGet: { path: /health/ready, port: http }
  periodSeconds: 10
{{- end -}}

{{- define "smartshop.podSpecCommon" -}}
{{- with .Values.imagePullSecrets }}
imagePullSecrets: {{ toYaml . | nindent 2 }}
{{- end }}
securityContext: {{ toYaml .Values.podSecurityContext | nindent 2 }}
{{- with .Values.nodeSelector }}
nodeSelector: {{ toYaml . | nindent 2 }}
{{- end }}
{{- with .Values.tolerations }}
tolerations: {{ toYaml . | nindent 2 }}
{{- end }}
{{- with .Values.affinity }}
affinity: {{ toYaml . | nindent 2 }}
{{- end }}
{{- end -}}
