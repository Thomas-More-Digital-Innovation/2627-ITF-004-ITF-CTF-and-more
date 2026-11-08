{{- define "ctf-edge.tls" -}}
{{- and .Values.domain .Values.acme.cloudflare .Values.acme.email -}}
{{- end -}}
{{- define "ctf-edge.issuer" -}}
{{- if .Values.acme.staging }}letsencrypt-staging{{ else }}letsencrypt{{ end -}}
{{- end -}}
