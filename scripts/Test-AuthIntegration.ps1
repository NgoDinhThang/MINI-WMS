[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoBuild,
    [string] $Filter
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$previousPostgres = $env:MINIWMS_TEST_POSTGRES
$previousRedis = $env:MINIWMS_TEST_REDIS
try {
    if ([string]::IsNullOrWhiteSpace($env:MINIWMS_TEST_POSTGRES)) {
        [xml] $project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\MiniWms.Api\MiniWms.Api.csproj') -Raw
        $secretId = [string]$project.Project.PropertyGroup.UserSecretsId
        $secretPath = Join-Path $env:APPDATA ('Microsoft\UserSecrets\' + $secretId + '\secrets.json')
        $settings = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
        $env:MINIWMS_TEST_POSTGRES = [string]$settings.'ConnectionStrings:WmsDatabase'
        if ([string]::IsNullOrWhiteSpace($env:MINIWMS_TEST_POSTGRES)) { throw 'Configure MINIWMS_TEST_POSTGRES or the API database User Secret.' }
        if ([string]::IsNullOrWhiteSpace($env:MINIWMS_TEST_REDIS)) { $env:MINIWMS_TEST_REDIS = [string]$settings.'ConnectionStrings:Redis' }
    }
    $testProject = Join-Path $repositoryRoot 'tests\MiniWms.Api.IntegrationTests\MiniWms.Api.IntegrationTests.csproj'
    $testArguments = @('test', $testProject, '-c', $Configuration)
    if ($NoBuild) { $testArguments += '--no-build' }
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $testArguments += @('--filter', $Filter) }
    & dotnet @testArguments
    $testExitCode = $LASTEXITCODE
} finally {
    if ($null -eq $previousPostgres) { Remove-Item Env:MINIWMS_TEST_POSTGRES -ErrorAction SilentlyContinue } else { $env:MINIWMS_TEST_POSTGRES = $previousPostgres }
    if ($null -eq $previousRedis) { Remove-Item Env:MINIWMS_TEST_REDIS -ErrorAction SilentlyContinue } else { $env:MINIWMS_TEST_REDIS = $previousRedis }
}
exit $testExitCode
