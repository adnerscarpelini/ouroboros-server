param([Parameter(Mandatory)][string] $Directory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
$ancestor = [IO.DirectoryInfo]::new($root)
while ($null -ne $ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) { throw 'Chaves devem ficar fora do checkout' }
    $ancestor = $ancestor.Parent
}
if (Test-Path -LiteralPath $root) { throw 'Use um diretório novo para não substituir chaves retidas' }
[void][IO.Directory]::CreateDirectory($root)
if ($IsWindows) {
    $acl = Get-Acl -LiteralPath $root
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $root -AclObject $acl
} else { [IO.File]::SetUnixFileMode($root, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
$keyId = [Guid]::NewGuid().ToString('N')
$keyPath = Join-Path $root ($keyId + '.key')
$manifestPath = Join-Path $root 'fingerprint.json'
[IO.File]::WriteAllBytes($keyPath, [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
[IO.File]::WriteAllText($manifestPath, (@{ activeKeyId=$keyId; keys=@(@{keyId=$keyId;keyPath=$keyPath}) } | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
if (-not $IsWindows) {
    foreach ($path in @($keyPath, $manifestPath)) { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
}
Write-Output 'Chave de impressão autenticada de Notifications gerada; não distribua para Identity.'
