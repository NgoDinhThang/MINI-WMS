[CmdletBinding()]
param(
    [ValidateSet('Admin', 'Staff', 'Staff2', 'Banned')]
    [string] $Account = 'Staff'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
[xml] $project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\MiniWms.Api\MiniWms.Api.csproj') -Raw
$secretId = [string]$project.Project.PropertyGroup.UserSecretsId
if ([string]::IsNullOrWhiteSpace($secretId)) { throw 'The API User Secrets identifier is missing.' }
$secretPath = Join-Path $env:APPDATA ('Microsoft\UserSecrets\' + $secretId + '\secrets.json')
$settings = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
$emails = @{ Admin = 'admin@mini-wms.test'; Staff = 'staff1@mini-wms.test'; Staff2 = 'staff2@mini-wms.test'; Banned = 'staff.banned@mini-wms.test' }
$passwordSetting = if ($Account -eq 'Admin') { 'MockData:AdminPassword' } else { 'MockData:StaffPassword' }
$passwordProperty = $settings.PSObject.Properties[$passwordSetting]
if ($null -eq $passwordProperty -or [string]::IsNullOrWhiteSpace([string]$passwordProperty.Value)) { throw "Development setting $passwordSetting is missing. Configure it before seeding." }
$securePassword = ConvertTo-SecureString -String ([string]$passwordProperty.Value) -AsPlainText -Force
[System.Management.Automation.PSCredential]::new($emails[$Account], $securePassword)
