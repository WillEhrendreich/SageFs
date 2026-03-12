@echo off
rem SageFs with OpenTelemetry export to a local OTLP collector (e.g. Jaeger, Grafana Agent).
rem
rem Requires a running OTLP receiver on localhost:4318 (HTTP/Protobuf).
rem Quick start with Jaeger all-in-one:
rem   docker run --rm -p 4318:4318 -p 16686:16686 jaegertracing/all-in-one:latest
rem   Then open http://localhost:16686 to view traces.
rem
rem Simpler alternative — structured logs go to file sink even without OTel:
rem   Just run SageFs normally; logs are written to
rem   %%LOCALAPPDATA%%\SageFs\sagefs.log (Windows) or ~/.local/share/sagefs/sagefs.log (Linux).
rem   Set OTEL_EXPORTER_OTLP_ENDPOINT only when you want traces/metrics in a collector.
set OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
set OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
set OTEL_SERVICE_NAME=sagefs
cd /d C:\Code\Repos\SageFs
SageFs
