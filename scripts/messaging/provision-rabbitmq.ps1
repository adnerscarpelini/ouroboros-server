# Provisionamento explícito, idempotente, sem remover filas, mensagens ou volumes.
# Segredos pelo ambiente ou arquivo externo; não usar -Verbose/Debug/transcript durante a operação.
param([string] $ManagementUrl = 'http://127.0.0.1:15672', [switch] $LocalDevelopment, [string] $EnvFile)
$ErrorActionPreference = 'Stop'
$required = @('RABBITMQ_ADMIN_USER', 'RABBITMQ_ADMIN_PASSWORD', 'IDENTITY_BROKER_PASSWORD',
    'NOTIFICATIONS_BROKER_PASSWORD', 'NOTIFICATIONS_FAILURES_BROKER_PASSWORD')
if ($EnvFile) {
    # Só dados, nunca Invoke-Expression/dot-source; não importar credenciais de banco ou outras variáveis.
    foreach ($line in [IO.File]::ReadAllLines([IO.Path]::GetFullPath($EnvFile))) {
        if ($line.TrimStart().StartsWith('#') -or -not $line.Contains('=')) { continue }
        $pair = $line.Split('=', 2)
        $name = $pair[0].Trim()
        if ($name -notin $required -or [Environment]::GetEnvironmentVariable($name)) { continue }
        $value = $pair[1].Trim()
        if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'")))) { $value = $value.Substring(1, $value.Length - 2) }
        [Environment]::SetEnvironmentVariable($name, $value)
    }
}
foreach ($name in $required) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Variável obrigatória ausente: $name" }
}
$passwords = @($env:RABBITMQ_ADMIN_PASSWORD, $env:IDENTITY_BROKER_PASSWORD,
    $env:NOTIFICATIONS_BROKER_PASSWORD, $env:NOTIFICATIONS_FAILURES_BROKER_PASSWORD)
if (($passwords | Select-Object -Unique).Count -ne 4 -or ($passwords | Where-Object Length -lt 32).Count -gt 0) {
    throw 'Use quatro senhas distintas de pelo menos 32 caracteres'
}
if (($passwords | Where-Object { $_.StartsWith('troque-') }).Count -gt 0) { throw 'Substitua os placeholders por senhas aleatórias' }
if ($env:RABBITMQ_ADMIN_USER -eq 'guest') { throw 'Usuário guest não é permitido' }
$uri = [Uri]$ManagementUrl
if ($uri.Scheme -ne 'https' -and -not $uri.IsLoopback -and -not $LocalDevelopment) { throw 'HTTP permitido somente em desenvolvimento local explícito' }
$basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("$($env:RABBITMQ_ADMIN_USER):$($env:RABBITMQ_ADMIN_PASSWORD)"))
$headers = @{ Authorization = "Basic $basic" }
function Invoke-Broker([string] $method, [string] $path, $body) {
    try {
        $arguments = @{ Method = $method; Uri = "$($ManagementUrl.TrimEnd('/'))/api/$path";
            Headers = $headers; TimeoutSec = 15 }
        if ($null -ne $body) {
            $arguments.ContentType = 'application/json'
            $arguments.Body = $body | ConvertTo-Json -Depth 12 -Compress
        }
        $null = Invoke-RestMethod @arguments
    } catch { throw "Falha no provisionamento RabbitMQ ($method $path); detalhes sensíveis omitidos" }
}

Invoke-Broker PUT 'vhosts/ouroboros' @{}
$users = @(
    @{ Name = 'identity_publisher'; Password = $env:IDENTITY_BROKER_PASSWORD; Write = '^ouroboros\.notifications$'; Read = '^$' },
    @{ Name = 'notifications_consumer'; Password = $env:NOTIFICATIONS_BROKER_PASSWORD; Write = '^$'; Read = '^notifications\.email\.v1$' },
    @{ Name = 'notifications_failures'; Password = $env:NOTIFICATIONS_FAILURES_BROKER_PASSWORD; Write = '^ouroboros\.notification-failures$'; Read = '^$' }
)
foreach ($user in $users) {
    Invoke-Broker PUT "users/$($user.Name)" @{ password = $user.Password; tags = '' }
    Invoke-Broker PUT "permissions/ouroboros/$($user.Name)" @{ configure = '^$'; write = $user.Write; read = $user.Read }
}
# O operador declara toda a topologia. Runtime não recebe configure nem credencial administrativa.
$exchanges = @('ouroboros.notifications', 'ouroboros.notification-failures')
foreach ($exchange in $exchanges) {
    Invoke-Broker PUT "exchanges/ouroboros/$exchange" @{ type = 'direct'; durable = $true; auto_delete = $false; internal = $false; arguments = @{} }
}
# Sem TTL/DLX no corpo original: expiração/classificação durável são responsabilidade da #87.
# reject-publish preserva mensagens existentes e devolve pressão ao publicador confirmado.
Invoke-Broker PUT 'queues/ouroboros/notifications.email.v1' @{
    durable = $true; auto_delete = $false
    arguments = @{ 'x-queue-type' = 'quorum'; 'x-max-length-bytes' = 67108864; 'x-overflow' = 'reject-publish'; 'x-delivery-limit' = -1 }
}
Invoke-Broker PUT 'queues/ouroboros/notifications.failures.v1' @{
    durable = $true; auto_delete = $false
    arguments = @{ 'x-queue-type' = 'quorum'; 'x-message-ttl' = 2592000000L; 'x-max-length-bytes' = 8388608; 'x-overflow' = 'reject-publish' }
}
Invoke-Broker POST 'bindings/ouroboros/e/ouroboros.notifications/q/notifications.email.v1' @{ routing_key = 'email.requested.v1'; arguments = @{} }
Invoke-Broker POST 'bindings/ouroboros/e/ouroboros.notification-failures/q/notifications.failures.v1' @{ routing_key = 'email.failed.v1'; arguments = @{} }
Write-Output 'Topologia RabbitMQ e permissões convergidas; nenhum dado removido.'
