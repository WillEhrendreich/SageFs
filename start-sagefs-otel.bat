@echo off
set OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
set OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
set OTEL_SERVICE_NAME=sagefs
cd /d C:\Code\Repos\SageFs
SageFs --proj SageFs.Tests\SageFs.Tests.fsproj
