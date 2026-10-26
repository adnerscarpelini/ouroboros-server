param([Parameter(Mandatory)][string] $Directory)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $target) { throw 'Use um diretório novo, externo ao código' }
$ancestor = [IO.DirectoryInfo]::new($target)
while ($null -ne $ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) { throw 'Chaves devem ficar fora de checkouts' }
    $ancestor = $ancestor.Parent
}
[void][IO.Directory]::CreateDirectory($target)
if ($IsWindows) {
    $acl = Get-Acl -LiteralPath $target
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $target -AclObject $acl
} else {
    [IO.File]::SetUnixFileMode($target, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
}
$path = Join-Path $target 'supersession.key'
$bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
try { [IO.File]::WriteAllBytes($path, $bytes) } finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
if (-not $IsWindows) { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
Write-Output 'Chave de substituição criada sem imprimir material secreto.'
