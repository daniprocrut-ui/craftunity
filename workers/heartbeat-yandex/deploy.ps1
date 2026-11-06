param(
    [Parameter(Mandatory = $true)][string]$ReadToken,
    [string]$Bucket = "craftunity-heartbeat",
    [string]$FunctionName = "craftunity-heartbeat",
    [string]$FolderId = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    if (-not $FolderId) {
        $FolderId = (yc config get folder-id).Trim()
    }
    "Папка: $FolderId"

    yc storage bucket create --name $Bucket --default-storage-class standard 2>$null

    $saId = (yc iam service-account create --name "$FunctionName-sa" --format json | ConvertFrom-Json).id
    "Сервисный аккаунт: $saId"
    yc resource-manager folder add-access-binding $FolderId --role storage.editor --subject "serviceAccount:$saId"

    $zip = Join-Path $env:TEMP "$FunctionName.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $root "index.js") -DestinationPath $zip -Force

    yc serverless function create --name $FunctionName 2>$null
    yc serverless function version create `
        --function-name $FunctionName `
        --runtime nodejs18 `
        --entrypoint index.handler `
        --memory 128m `
        --execution-timeout 10s `
        --source-path $zip `
        --service-account-id $saId `
        --environment "BUCKET=$Bucket,READ_TOKEN=$ReadToken"

    $functionId = (yc serverless function get --name $FunctionName --format json | ConvertFrom-Json).id
    "Функция: $functionId"

    $spec = (Get-Content -LiteralPath (Join-Path $root "openapi.yaml") -Raw).Replace('${FUNCTION_ID}', $functionId)
    $specPath = Join-Path $env:TEMP "$FunctionName-openapi.yaml"
    Set-Content -LiteralPath $specPath -Value $spec -Encoding UTF8

    yc serverless api-gateway create --name "$FunctionName-gw" --spec $specPath 2>$null
    $gw = yc serverless api-gateway get --name "$FunctionName-gw" --format json | ConvertFrom-Json
    "Приёмник: https://$($gw.domain)/"
}
finally {
    Pop-Location
}
