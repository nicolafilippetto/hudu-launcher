# Prova l'endpoint IPAM di Hudu (/api/v1/ip_addresses): solo letture (GET).
# Uso: powershell -ExecutionPolicy Bypass -File .\prova-ipam.ps1   (oppure incollare il contenuto)
# La chiave API viene chiesta a video e non viene salvata ne' stampata.
# Nota: l'endpoint NON accetta page/page_size (risponde 400) e restituisce tutto in un array.

$CompanyId = 0
$HuduUrl = "https://yourcompany.huducloud.com"
$sec = Read-Host "Chiave API di Hudu" -AsSecureString
$key = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
$hdr = @{ "x-api-key" = $key; "Accept" = "application/json" }
$queries = @("")
if ($CompanyId -gt 0) { $queries += "?company_id=$CompanyId" }
foreach ($q in $queries) {
  Write-Host ""
  Write-Host "=== GET /api/v1/ip_addresses$q" -ForegroundColor Cyan
  try {
    $r = Invoke-WebRequest -Uri "$HuduUrl/api/v1/ip_addresses$q" -Headers $hdr -UseBasicParsing -TimeoutSec 60
    $items = @($r.Content | ConvertFrom-Json)
    $linked = @($items | Where-Object { $_.asset_id -and -not $_.discarded_at })
    Write-Host ("200 - indirizzi: " + $items.Count + ", collegati a un asset: " + $linked.Count) -ForegroundColor Green
    $linked | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_.address + "  asset " + $_.asset_id + " " + $_.asset_name + "  (" + $_.status + ")") }
  } catch {
    $code = $null; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    Write-Host ("ERRORE " + $code + " : " + $_.ErrorDetails.Message) -ForegroundColor Red
  }
}
$key = $null
