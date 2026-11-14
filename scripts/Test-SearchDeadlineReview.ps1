param([Parameter(Mandatory=$true)][string]$Executable, [Parameter(Mandatory=$true)][string]$ScratchRoot)
$ErrorActionPreference = 'Stop'
$ownedRoot = [IO.Path]::GetFullPath($ScratchRoot)
if (Test-Path -LiteralPath $ownedRoot) { throw 'The search fixture needs a new owned folder.' }
New-Item -ItemType Directory -Path $ownedRoot | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $ownedRoot '000-match.txt'), 'deadline-needle')
    $large = Join-Path $ownedRoot 'large-source.txt'
    $block = [Text.Encoding]::ASCII.GetBytes(('x' * 4095) + "`n")
    $stream = [IO.File]::Create($large)
    try { for ($i=0; $i -lt 16384; $i++) { $stream.Write($block, 0, $block.Length) } }
    finally { $stream.Dispose() }
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SearchDeadlineLinks { [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool CreateHardLink(string path, string existing, IntPtr attributes); }'
    for ($i=0; $i -lt 128; $i++) {
        if (![SearchDeadlineLinks]::CreateHardLink((Join-Path $ownedRoot ("slow-{0:d3}.txt" -f $i)), $large, [IntPtr]::Zero)) { throw 'Could not create the bounded search corpus.' }
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = [IO.Path]::GetFullPath($Executable)
    $start.Arguments = '--cli search "' + $ownedRoot.Replace('"', '\"') + '" --content deadline-needle --max-seconds 1'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) { $process.Kill(); throw 'The bounded search did not finish.' }
        $code = $process.ExitCode
        $output = $outputTask.GetAwaiter().GetResult()
        $errorText = $errorTask.GetAwaiter().GetResult()
    }
    finally { $process.Dispose() }
    if ($code -ne 0) { throw ('Bounded search failed: ' + $errorText) }
    $result = $output | ConvertFrom-Json
    if (!$result.stoppedEarly -or @($result.results).Count -lt 1 -or !($result.results.path -match '000-match.txt')) { throw 'The deadline discarded collected search matches or did not report the early stop.' }
    Write-Output 'PASS search deadline retains matches and reports stoppedEarly'
}
finally {
    if ([IO.Path]::GetFullPath($ownedRoot) -ne $ownedRoot -or $ownedRoot -eq [IO.Path]::GetPathRoot($ownedRoot)) { throw 'The fixture cleanup target could not be verified.' }
    Remove-Item -LiteralPath $ownedRoot -Recurse -Force
}
