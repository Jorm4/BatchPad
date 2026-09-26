<#
.SYNOPSIS
Deploys the site.
#>
param(
    [switch]$Force,
    [ValidateSet('dev', 'prod')]
    [string]$Target = 'dev',
    [int]$Retries = 3
)

throw "tool.ps1 must never run during detection"
