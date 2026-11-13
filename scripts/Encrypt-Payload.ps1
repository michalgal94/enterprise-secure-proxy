param(
    [Parameter(Mandatory=$true)][string]$ClientId,
    [Parameter(Mandatory=$true)][string]$Json,
    [string]$KeyReference = 'LOCAL'
)
# Requires PowerShell 7 for AesGcm.
$keyText = [Environment]::GetEnvironmentVariable('PROXY_KEY_' + $KeyReference)
if (-not $keyText) { throw 'Set PROXY_KEY_<reference> to a base64 32-byte key.' }
$keyBytes = [Convert]::FromBase64String($keyText)
$plainBytes = [Text.Encoding]::UTF8.GetBytes($Json)
$nonceBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
$tagBytes = [byte[]]::new(16)
$cipherBytes = [byte[]]::new($plainBytes.Length)
$aes = [Security.Cryptography.AesGcm]::new($keyBytes, 16)
try {
    $aes.Encrypt($nonceBytes, $plainBytes, $cipherBytes, $tagBytes, [Text.Encoding]::UTF8.GetBytes($ClientId))
    @{ clientId = $ClientId; encrypted = @{ nonce = [Convert]::ToBase64String($nonceBytes); ciphertext = [Convert]::ToBase64String($cipherBytes); tag = [Convert]::ToBase64String($tagBytes) } } | ConvertTo-Json -Depth 4 -Compress
}
finally { $aes.Dispose(); [Array]::Clear($keyBytes, 0, $keyBytes.Length); [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
