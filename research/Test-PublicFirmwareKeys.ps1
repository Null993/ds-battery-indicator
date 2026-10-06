param(
    [Parameter(Mandatory=$true)][string[]]$FirmwareFiles,
    [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
# Offline, bounded compatibility hypotheses only. These are public DS4 keys, not verified DualSense keys.
# Source: https://www.psdevwiki.com/ps4/Keys#Dualshock_4_Keys
$candidates = @(
    @{ name='Jedi-master-v1'; hex='9B03D4FB5FEC1A2373462C45E4BC72A6' },
    @{ name='Jedi-master-v2'; hex='DBE167BD04C2D1271C862489C7488B45' }
)
function Decrypt-Cbc([byte[]]$data, [byte[]]$key, [byte[]]$iv) {
    $aes=[Security.Cryptography.Aes]::Create()
    try {
        $aes.Mode=[Security.Cryptography.CipherMode]::CBC
        $aes.Padding=[Security.Cryptography.PaddingMode]::None
        $decryptor=$aes.CreateDecryptor($key,$iv)
        try { return ,$decryptor.TransformFinalBlock($data,0,$data.Length) }
        finally { $decryptor.Dispose() }
    } finally { $aes.Dispose() }
}
# NIST SP800-38A F.2.2 AES-128 CBC example, first block.
$control=Decrypt-Cbc ([Convert]::FromHexString('7649abac8119b246cee98e9b12e9197d')) ([Convert]::FromHexString('2b7e151628aed2a6abf7158809cf4f3c')) ([Convert]::FromHexString('000102030405060708090a0b0c0d0e0f'))
if ([Convert]::ToHexString($control) -ne '6BC1BEE22E409F96E93D7E117393172A') { throw 'AES implementation control failed' }
$results=@()
foreach ($file in $FirmwareFiles) {
    $data=[IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $file))
    foreach ($candidate in $candidates) {
        foreach ($offset in @(0x80,0x100,0x200,0x1000)) {
            $length=[Math]::Min(65536,[Math]::Floor(($data.Length-$offset)/16)*16)
            if ($length -lt 64) { continue }
            $slice=[byte[]]$data[$offset..($offset+$length-1)]
            $decoded=Decrypt-Cbc $slice ([Convert]::FromHexString($candidate.hex)) (New-Object byte[] 16)
            $hist=New-Object int[] 256
            foreach ($b in $decoded) { $hist[$b]++ }
            $entropy=0.0
            foreach ($n in $hist) { if ($n -gt 0) { $p=$n/$decoded.Length; $entropy-=$p*[Math]::Log2($p) } }
            $ascii=[Text.Encoding]::ASCII.GetString($decoded)
            $markers=@([regex]::Matches($ascii,'(?i)battery|charging|voltage|current|Copyright|Sony|Banana|Venom|Betty') | ForEach-Object { @{ offset=$_.Index; text=$_.Value } })
            $results += [pscustomobject]@{
                file=[IO.Path]::GetFileName($file); sourceSha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
                keyName=$candidate.name; assumedOffset=$offset; testedBytes=$length; assumedMode='AES-128-CBC'; assumedIv='zero'
                decodedSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($decoded))
                decodedPrefixHex=[Convert]::ToHexString($decoded[0..63]); decodedEntropy=$entropy; markerHits=$markers
                verifiedPlaintext=$false
            }
        }
    }
}
$result=[pscustomobject]@{
    utc=[DateTimeOffset]::UtcNow.ToString('o'); aesControlPassed=$true; candidates=$results
    limitation='This only tests two public DS4 keys at four assumed offsets with zero IV. Random AES output is not recovered firmware. No architecture, integrity or current/SOC routine is established; no device is accessed.'
}
$result | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 -LiteralPath $Output
Write-Output ('Saved '+$results.Count+' bounded offline hypotheses to '+$Output)
