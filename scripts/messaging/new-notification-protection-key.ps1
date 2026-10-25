param([Parameter(Mandatory)][string] $Directory)
$ErrorActionPreference = 'Stop'
$keyDirectory = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $keyDirectory) { throw 'Use um diretório novo, sem sobrescrever chaves existentes' }
$ancestor = [IO.DirectoryInfo]::new($keyDirectory)
while ($null -ne $ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) { throw 'Chaves devem ficar fora de checkouts' }
    $ancestor = $ancestor.Parent
}
[void][IO.Directory]::CreateDirectory($keyDirectory)
if ($IsWindows) {
    $acl = Get-Acl -LiteralPath $keyDirectory
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $keyDirectory -AclObject $acl
} else {
    [IO.File]::SetUnixFileMode($keyDirectory, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
}
$keyId = 'notifications-' + [Guid]::NewGuid().ToString('N')
$privatePath = Join-Path $keyDirectory "$keyId.pem"
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    [IO.File]::WriteAllText($privatePath, $rsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
    $public = $rsa.ExportParameters($false)
    $publicManifest = @{
        activeKeyId = $keyId
        keys = @(@{ keyId = $keyId
            n = [Convert]::ToBase64String($public.Modulus).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            e = [Convert]::ToBase64String($public.Exponent).TrimEnd('=').Replace('+', '-').Replace('/', '_') })
    }
    $privateManifest = @{ keys = @(@{ keyId = $keyId; privateKeyPath = $privatePath }) }
    [IO.File]::WriteAllText((Join-Path $keyDirectory 'public.json'), ($publicManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $keyDirectory 'private.json'), ($privateManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    if (-not $IsWindows) {
        foreach ($name in @("$keyId.pem", 'private.json', 'public.json')) {
            [IO.File]::SetUnixFileMode((Join-Path $keyDirectory $name), [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }
    }
} finally { $rsa.Dispose() }
Write-Output 'Par de proteção criado. Entregue somente public.json ao produtor; privada exclusiva de Notifications.'
