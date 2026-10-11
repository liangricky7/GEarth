param([Parameter(Mandatory)][string]$Csv, [Parameter(Mandatory)][string]$OutputJson)
$ErrorActionPreference = 'Stop'
$rows = @(Import-Csv -LiteralPath $Csv)
if (!$rows.Count) { throw 'Empty event CSV' }
$events = @($rows | ForEach-Object {
    $event = $_.payload_json | ConvertFrom-Json -ErrorAction Stop
    if ($event.event_id -ne $_.event_id -or $event.run_id -ne $_.run_id -or
        [long]$event.tick -ne [long]$_.tick -or [long]$event.seq -ne [long]$_.event_sequence) {
        throw 'CSV columns disagree with event payload'
    }
    $event
})
if (@($events.run_id | Sort-Object -Unique).Count -ne 1) { throw 'Export exactly one run' }
if (@($events.event_id | Sort-Object -Unique).Count -ne $events.Count) { throw 'Duplicate event IDs' }
$json = ConvertTo-Json -InputObject $events -Depth 80
# Serialize fully before writing; UTF-8 without a BOM also works in older PowerShell.
[System.IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputJson), $json, [System.Text.UTF8Encoding]::new($false))
Write-Output "Exported $($events.Count) events from $($events[0].run_id)"
