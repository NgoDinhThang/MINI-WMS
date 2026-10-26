[CmdletBinding()]
param(
    [ValidateSet('Admin', 'Staff', 'Staff2', 'Banned')]
    [string] $Account = 'Staff',
    [string] $BaseUrl = 'http://localhost:5080'
)
$ErrorActionPreference = 'Stop'
$endpoint = [Uri]::new($BaseUrl, [UriKind]::Absolute)
if ($endpoint.Scheme -ne 'https' -and -not ($endpoint.Scheme -eq 'http' -and $endpoint.IsLoopback)) { throw 'Use HTTPS or a loopback development endpoint for demo credentials.' }
$credential = & (Join-Path $PSScriptRoot 'Get-DemoCredential.ps1') -Account $Account
$body = @{ email = $credential.UserName; password = $credential.GetNetworkCredential().Password } | ConvertTo-Json -Compress
# Assign the result to a variable: it contains access/refresh credentials for your API client.
Invoke-RestMethod -Method Post -Uri ($BaseUrl.TrimEnd('/') + '/auth/login') -ContentType 'application/json' -Body $body
