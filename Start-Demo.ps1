[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\AiHarnessDemo\AiHarnessDemo.csproj'

Write-Host 'Starting AI Harness Studio at http://localhost:5283' -ForegroundColor Cyan
dotnet run --project $project --launch-profile http
