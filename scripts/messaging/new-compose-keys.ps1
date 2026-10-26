# Gera os diretórios externos de chaves criptográficas para execução no Docker Compose (Identity e Notifications),
# fora de qualquer checkout Git e com caminhos internos absolutos (/run/ouroboros-keys/identity e /run/ouroboros-keys/notifications).
param([Parameter(Mandatory)][string] $Directory)
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $root) { throw 'Escolha um diretório novo, sem sobrescrever chaves existentes' }
$ancestor = [IO.DirectoryInfo]::new($root)
while ($null -ne $ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) { throw 'Chaves devem ficar fora de checkouts' }
    $ancestor = $ancestor.Parent
}

$identityDir = Join-Path $root 'identity'
$notificationsDir = Join-Path $root 'notifications'
[void][IO.Directory]::CreateDirectory($identityDir)
[void][IO.Directory]::CreateDirectory($notificationsDir)

function Protect-Directory([string] $path) {
    if ($IsWindows) {
        $acl = Get-Acl -LiteralPath $path
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent().User,
            'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $path -AclObject $acl
    } else {
        # Permite travessia de leitura ao UID não-root do container em bind mount local restrito ao operador.
        [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute -bor
            [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherExecute)
    }
}

function Protect-File([string] $path) {
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor
            [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::OtherRead)
    }
}

Protect-Directory $root
Protect-Directory $identityDir
Protect-Directory $notificationsDir

$utf8 = [Text.UTF8Encoding]::new($false)

# 1. Assinatura JWT (exclusiva de Identity)
$jwtKid = 'identity-' + [Guid]::NewGuid().ToString('N')
$jwtPemName = "$jwtKid.pem"
$jwtHostPem = Join-Path $identityDir $jwtPemName
$jwtRsa = [Security.Cryptography.RSA]::Create(3072)
try {
    [IO.File]::WriteAllText($jwtHostPem, $jwtRsa.ExportPkcs8PrivateKeyPem(), $utf8)
    $jwtPublic = $jwtRsa.ExportParameters($false)
    $jwtManifest = @{
        activeKid = $jwtKid
        privateKeyPath = "/run/ouroboros-keys/identity/$jwtPemName"
        keys = @(@{
            kid = $jwtKid
            n = [Convert]::ToBase64String($jwtPublic.Modulus).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            e = [Convert]::ToBase64String($jwtPublic.Exponent).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        })
    }
    $jwtRingPath = Join-Path $identityDir 'ring.json'
    [IO.File]::WriteAllText($jwtRingPath, ($jwtManifest | ConvertTo-Json -Depth 6), $utf8)
    Protect-File $jwtHostPem
    Protect-File $jwtRingPath
} finally { $jwtRsa.Dispose() }

# 2. Chave HMAC de substituição de intenções de e-mail (exclusiva de Identity)
$supersessionPath = Join-Path $identityDir 'supersession.key'
$supersessionBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
try { [IO.File]::WriteAllBytes($supersessionPath, $supersessionBytes) }
finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($supersessionBytes) }
Protect-File $supersessionPath

# 3. Par RSA de proteção de e-mail (pública em Identity, privada exclusiva em Notifications)
$encKeyId = 'notifications-' + [Guid]::NewGuid().ToString('N')
$encPemName = "$encKeyId.pem"
$encHostPem = Join-Path $notificationsDir $encPemName
$encRsa = [Security.Cryptography.RSA]::Create(3072)
try {
    [IO.File]::WriteAllText($encHostPem, $encRsa.ExportPkcs8PrivateKeyPem(), $utf8)
    $encPublic = $encRsa.ExportParameters($false)
    $publicManifest = @{
        activeKeyId = $encKeyId
        keys = @(@{
            keyId = $encKeyId
            n = [Convert]::ToBase64String($encPublic.Modulus).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            e = [Convert]::ToBase64String($encPublic.Exponent).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        })
    }
    $privateManifest = @{
        keys = @(@{
            keyId = $encKeyId
            privateKeyPath = "/run/ouroboros-keys/notifications/$encPemName"
        })
    }
    $publicPath = Join-Path $identityDir 'public.json'
    $privatePath = Join-Path $notificationsDir 'private.json'
    [IO.File]::WriteAllText($publicPath, ($publicManifest | ConvertTo-Json -Depth 6), $utf8)
    [IO.File]::WriteAllText($privatePath, ($privateManifest | ConvertTo-Json -Depth 6), $utf8)
    Protect-File $encHostPem
    Protect-File $publicPath
    Protect-File $privatePath
} finally { $encRsa.Dispose() }

# 4. Chave HMAC de fingerprint de deduplicação (exclusiva de Notifications)
$fpKeyId = [Guid]::NewGuid().ToString('N')
$fpKeyName = "$fpKeyId.key"
$fpHostKey = Join-Path $notificationsDir $fpKeyName
$fpBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
try { [IO.File]::WriteAllBytes($fpHostKey, $fpBytes) }
finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($fpBytes) }
$fpManifest = @{
    activeKeyId = $fpKeyId
    keys = @(@{
        keyId = $fpKeyId
        keyPath = "/run/ouroboros-keys/notifications/$fpKeyName"
    })
}
$fpManifestPath = Join-Path $notificationsDir 'fingerprint.json'
[IO.File]::WriteAllText($fpManifestPath, ($fpManifest | ConvertTo-Json -Depth 6), $utf8)
Protect-File $fpHostKey
Protect-File $fpManifestPath

Write-Output 'Chaves externas de Compose criadas para Identity e Notifications sem imprimir material secreto.'
