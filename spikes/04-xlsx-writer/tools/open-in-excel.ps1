<#
.SYNOPSIS
    Opens an XLSX file in Excel (read-only, alerts enabled) and checks the
    sheet names, the used row and column counts and a list of probe cells.

.DESCRIPTION
    Excel runs in a child PowerShell process. An open that does not finish
    within the timeout is reported as "blocked" (Excel is most likely showing
    a repair or other prompt); only the Excel process started by this check is
    stopped, and the file must then be opened by a person to see what Excel
    asks. Nothing is saved. Prints one JSON line (file name only).

.PARAMETER Path
    The XLSX file.

.PARAMETER Probes
    The probe file written by "XlsxWriterSpike inspect --probes".

.PARAMETER TimeoutSeconds
    Time allowed for the whole open-and-check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Probes,
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$file = (Resolve-Path -LiteralPath $Path).ProviderPath
$probeFile = (Resolve-Path -LiteralPath $Probes).ProviderPath
$name = [IO.Path]::GetFileName($file)
$spec = Get-Content -LiteralPath $probeFile -Raw -Encoding UTF8 | ConvertFrom-Json
$work = Join-Path ([IO.Path]::GetTempPath()) ('excel-open-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$resultFile = Join-Path $work 'result.json'
$pidFile = Join-Path $work 'excel.pid'
$childScript = Join-Path $work 'child.ps1'

$child = @'
param($File, $ProbeFile, $ResultFile, $PidFile)
$ErrorActionPreference = 'Stop'
function Short([string]$text) { if ($text.Length -le 40) { return $text } return $text.Substring(0, 40) + '...(length ' + $text.Length + ')' }
Add-Type -Namespace OpenCheck -Name Native -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint processId);'
$spec = Get-Content -LiteralPath $ProbeFile -Raw -Encoding UTF8 | ConvertFrom-Json
$result = @{ worksheets = 0; sheets = @(); probes = 0; mismatches = @(); error = $null }
$excel = New-Object -ComObject Excel.Application
try {
    [uint32]$excelPid = 0
    [void][OpenCheck.Native]::GetWindowThreadProcessId([IntPtr]$excel.Hwnd, [ref]$excelPid)
    Set-Content -LiteralPath $PidFile -Value $excelPid -Encoding Ascii
    $excel.Visible = $false
    $excel.DisplayAlerts = $true
    $workbook = $excel.Workbooks.Open($File, 0, $true)
    try {
        $result.worksheets = $workbook.Worksheets.Count
        foreach ($sheet in $workbook.Worksheets) {
            $used = $sheet.UsedRange
            $result.sheets += @{ name = $sheet.Name; rows = $used.Rows.Count; columns = $used.Columns.Count }
        }
        $inv = [Globalization.CultureInfo]::InvariantCulture
        foreach ($probe in $spec.probes) {
            $cell = $workbook.Worksheets.Item($probe.sheet).Range($probe.cell)
            $result.probes++
            $value = $cell.Value2
            $hasFormula = [bool]$cell.HasFormula
            $ok = -not $hasFormula
            if ($probe.kind -eq 'text') {
                $ok = $ok -and ($value -is [string]) -and ($value -ceq $probe.expected)
            } else {
                $ok = $ok -and ($value -is [double]) -and ([decimal]$value -eq [decimal]::Parse($probe.expected, [Globalization.NumberStyles]::Float, $inv))
            }
            if (-not $ok) {
                $result.mismatches += ('{0}!{1} expected {2} [{3}] found [{4}] hasFormula={5}' -f $probe.sheet, $probe.cell, $probe.kind, (Short $probe.expected), (Short ([string]$value)), $hasFormula)
            }
        }
    } finally {
        $workbook.Close($false)
    }
} catch {
    $result.error = $_.Exception.Message
} finally {
    $excel.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
}
$result | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath $ResultFile -Encoding UTF8
'@
Set-Content -LiteralPath $childScript -Value $child -Encoding UTF8

$hostPath = (Get-Process -Id $PID).Path
$before = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
    ('"' + $childScript + '"'), ('"' + $file + '"'), ('"' + $probeFile + '"'), ('"' + $resultFile + '"'), ('"' + $pidFile + '"'))
$process = Start-Process -FilePath $hostPath -ArgumentList $arguments -PassThru -WindowStyle Hidden
$clock = [Diagnostics.Stopwatch]::StartNew()
$finished = $process.WaitForExit($TimeoutSeconds * 1000)
$clock.Stop()

$output = [ordered]@{ file = $name; result = 'fail'; seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1); worksheets = $null; sheets = @(); probes = 0; mismatches = @(); error = $null }
if (-not $finished) {
    $output.result = 'blocked'
    $output.error = 'Excel did not finish within ' + $TimeoutSeconds + ' s; open the file by hand to see what Excel asks'
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    $started = @()
    if (Test-Path -LiteralPath $pidFile) {
        $started = @([int](Get-Content -LiteralPath $pidFile))
    } else {
        $started = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | ForEach-Object { $_.Id })
    }
    foreach ($id in $started) {
        if ($id -gt 0) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    }
} elseif (-not (Test-Path -LiteralPath $resultFile)) {
    $output.error = 'the Excel check produced no result'
} else {
    $child = Get-Content -LiteralPath $resultFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $output.worksheets = $child.worksheets
    $output.sheets = @($child.sheets)
    $output.probes = $child.probes
    $output.mismatches = @($child.mismatches)
    $output.error = $child.error
    $problems = @()
    $expected = @($spec.sheets)
    if ($child.worksheets -ne $expected.Count) { $problems += ('worksheets: expected ' + $expected.Count + ', found ' + $child.worksheets) }
    for ($i = 0; $i -lt [math]::Min($expected.Count, @($child.sheets).Count); $i++) {
        $e = $expected[$i]
        $f = @($child.sheets)[$i]
        if ($e.name -ne $f.name -or [long]$e.rows -ne [long]$f.rows -or [long]$e.columns -ne [long]$f.columns) {
            $problems += ('sheet ' + ($i + 1) + ': expected ' + $e.name + ' ' + $e.rows + 'x' + $e.columns + ', found ' + $f.name + ' ' + $f.rows + 'x' + $f.columns)
        }
    }
    $output.mismatches = @($output.mismatches) + $problems
    if ($null -eq $child.error -and @($output.mismatches).Count -eq 0 -and $child.probes -eq @($spec.probes).Count) {
        $output.result = 'pass'
    }
}

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
$output | ConvertTo-Json -Depth 5 -Compress
