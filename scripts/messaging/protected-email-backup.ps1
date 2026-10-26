# Protege um pg_dump custom já produzido pelo operador; não lê credenciais nem acessa bancos.
# Diretório novo/dedicado; purga automática só alcança arquivos registrados nesse formato.
param(
    [Parameter(Mandatory)][ValidateSet('Protect','Restore')][string] $Mode,
    [Parameter(Mandatory)][string] $SourceFile,
    [Parameter(Mandatory)][string] $Directory,
    [Parameter(Mandatory)][string] $KeyPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]{1,80}$')][string] $KeyId,
    [string] $RestoreFile,
    [switch] $RemoveSource
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
$source = [IO.Path]::GetFullPath($SourceFile)
$secretPath = [IO.Path]::GetFullPath($KeyPath)
if ($secretPath.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A chave deve ficar fora do diretório de backup'
}
$ancestor = [IO.DirectoryInfo]::new($root)
while ($null -ne $ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) { throw 'Backups devem ficar fora do checkout' }
    $ancestor = $ancestor.Parent
}
$marker = Join-Path $root '.email-intent-backups'
if (-not (Test-Path -LiteralPath $root)) {
    [void][IO.Directory]::CreateDirectory($root)
    if ($IsWindows) {
        $acl = Get-Acl -LiteralPath $root
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $root -AclObject $acl
    } else { [IO.File]::SetUnixFileMode($root, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
    [IO.File]::WriteAllText($marker, 'ouroboros-protected-backups-v1')
}
if ((Get-Item -LiteralPath $root).LinkType -or [IO.File]::ReadAllText($marker) -ne 'ouroboros-protected-backups-v1') {
    throw 'Diretório não reconhecido; nenhum backup existente é adotado ou removido'
}
$key = $null; $plain = $null
$auditId = [Guid]::NewGuid().ToString('N')
function Write-BackupAudit([string] $outcome) {
    $record = @{ operationId=$auditId; operation=$Mode; outcome=$outcome; at=[DateTimeOffset]::UtcNow.ToString('O'); actor=[Environment]::UserName }
    $auditPath = Join-Path $root ('access-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd') + '.jsonl')
    [IO.File]::AppendAllText($auditPath, ($record | ConvertTo-Json -Compress) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($auditPath, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
}
try {
    Write-BackupAudit 'started'
    $key = [IO.File]::ReadAllBytes($secretPath)
    if ($key.Length -ne 32) { throw 'Chave deve ter 32 bytes' }
    $maximumBytes = if ($Mode -eq 'Protect') { 67108864 } else { 90000000 }
    if ((Get-Item -LiteralPath $source).Length -gt $maximumBytes) { throw 'Backup excede o limite permitido' }
    if ($Mode -eq 'Protect') {
        $plain = [IO.File]::ReadAllBytes($source)
        if ($plain.Length -lt 5 -or [Text.Encoding]::ASCII.GetString($plain, 0, 5) -ne 'PGDMP') { throw 'Use um pg_dump no formato custom' }
        $created = [DateTimeOffset]::UtcNow.ToString('O')
        $expires = [DateTimeOffset]::Parse($created).AddDays(30).ToString('O')
        $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
        $cipher = [byte[]]::new($plain.Length); $tag = [byte[]]::new(16)
        $aad = [Text.Encoding]::UTF8.GetBytes("email-backup-v1|$KeyId|$created|$expires")
        $aes = [Security.Cryptography.AesGcm]::new($key, 16)
        try { $aes.Encrypt($nonce, $plain, $cipher, $tag, $aad) } finally { $aes.Dispose() }
        $archive = @{ version=1; keyId=$KeyId; createdAt=$created; expiresAt=$expires
            nonce=[Convert]::ToBase64String($nonce); tag=[Convert]::ToBase64String($tag); ciphertext=[Convert]::ToBase64String($cipher) }
        $name = 'email-intents-' + [DateTimeOffset]::Parse($created).ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N') + '.json'
        $path = Join-Path $root $name
        [IO.File]::WriteAllText($path, ($archive | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        if (-not $IsWindows) { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
        if ($RemoveSource) { Remove-Item -LiteralPath $source }
        Write-Output "Backup protegido: $name"
    } else {
        if ([string]::IsNullOrWhiteSpace($RestoreFile) -or (Test-Path -LiteralPath $RestoreFile)) { throw 'Informe RestoreFile novo' }
        $archive = [IO.File]::ReadAllText($source) | ConvertFrom-Json -DateKind String
        if ($archive.version -ne 1 -or $archive.keyId -ne $KeyId) { throw 'Formato ou chave inválidos' }
        $cipher = [Convert]::FromBase64String($archive.ciphertext)
        if ($cipher.Length -gt 67108864) { throw 'Backup excede o limite permitido' }
        $plain = [byte[]]::new($cipher.Length)
        $aad = [Text.Encoding]::UTF8.GetBytes("email-backup-v1|$KeyId|$($archive.createdAt)|$($archive.expiresAt)")
        $aes = [Security.Cryptography.AesGcm]::new($key, 16)
        try { $aes.Decrypt([Convert]::FromBase64String($archive.nonce), $cipher, [Convert]::FromBase64String($archive.tag), $plain, $aad) } finally { $aes.Dispose() }
        $created = [DateTimeOffset]::Parse($archive.createdAt); $expires = [DateTimeOffset]::Parse($archive.expiresAt)
        if ($expires -ne $created.AddDays(30) -or [DateTimeOffset]::UtcNow -ge $expires) { throw 'Backup expirado' }
        [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($RestoreFile), $plain)
        if (-not $IsWindows) { [IO.File]::SetUnixFileMode([IO.Path]::GetFullPath($RestoreFile), [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
        Write-Output 'Backup autenticado para restauração; mantenha processamento parado e reaplique expiração antes de habilitar.'
    }
    Write-BackupAudit 'succeeded'
} catch {
    Write-BackupAudit 'failed'
    throw 'Operação de backup protegido não concluída; detalhes sensíveis omitidos.'
}
finally {
    if ($key) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($key) }
    if ($plain) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($plain) }
}
